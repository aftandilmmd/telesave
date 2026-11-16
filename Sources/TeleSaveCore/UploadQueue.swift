import Foundation

/// Owns upload state: schedules sends with a concurrency limit, maps TDLib events back to items,
/// retries with backoff, honours FLOOD_WAIT, and persists itself.
public actor UploadQueue {
    public struct Settings: Equatable, Sendable {
        public var maxConcurrent: Int
        public var sendMediaCompressed: Bool
        public var groupAsAlbum: Bool
        public var caption: CaptionTemplate

        public init(maxConcurrent: Int = 2, sendMediaCompressed: Bool = false, groupAsAlbum: Bool = false, caption: CaptionTemplate = CaptionTemplate("")) {
            self.maxConcurrent = maxConcurrent
            self.sendMediaCompressed = sendMediaCompressed
            self.groupAsAlbum = groupAsAlbum
            self.caption = caption
        }
    }

    public typealias Observer = @Sendable ([UploadItem]) -> Void
    public typealias DimensionsProbe = @Sendable (String, SendKind) async -> MediaDimensions?

    public private(set) var items: [UploadItem]
    public private(set) var isPaused = true

    private var transport: UploadTransport
    /// Bumped when the transport is replaced; results from older sends are ignored.
    private var generation = 0
    private let store: QueueStore?
    private let retryPolicy: RetryPolicy
    private let probe: DimensionsProbe?
    private let now: @Sendable () -> Date
    private var settings: Settings
    private var observer: Observer?

    private var activeJobs: [UUID: Set<UUID>] = [:]
    private var itemJob: [UUID: UUID] = [:]
    private var messageToItem: [Int64: UUID] = [:]
    private var fileToItem: [Int32: UUID] = [:]
    /// Send results that arrived before `send()` returned the temporary message id.
    private var earlyEvents: [Int64: TelegramEvent] = [:]
    private var scopedURLs: [UUID: URL] = [:]
    private var wakeTask: Task<Void, Never>?
    private var activity: NSObjectProtocol?

    static let keepFinishedCount = 50
    static let keepFinishedInterval: TimeInterval = 24 * 60 * 60

    public init(
        transport: UploadTransport,
        store: QueueStore? = nil,
        settings: Settings = Settings(),
        retryPolicy: RetryPolicy = RetryPolicy(),
        probe: DimensionsProbe? = nil,
        now: @escaping @Sendable () -> Date = { Date() }
    ) {
        self.transport = transport
        self.store = store
        self.settings = settings
        self.retryPolicy = retryPolicy
        self.probe = probe
        self.now = now
        self.items = store?.load() ?? []
    }

    // MARK: - Public API

    public func setObserver(_ observer: Observer?) {
        self.observer = observer
        observer?(items)
    }

    public func update(settings: Settings) {
        self.settings = settings
        pump()
    }

    /// The queue starts paused; unpause once the user is logged in and the network is up.
    public func setPaused(_ paused: Bool) {
        isPaused = paused
        if !paused { pump() }
    }

    /// Swaps the Telegram client (e.g. after an API key rotation). In-flight uploads go back to the
    /// queue and restart on the new client; anything the old client reports later is ignored.
    public func replaceTransport(_ newTransport: UploadTransport) {
        generation += 1
        transport = newTransport
        for i in items.indices {
            if case .uploading = items[i].state {
                items[i].state = .queued
                items[i].messageId = nil
            }
        }
        activeJobs.removeAll()
        itemJob.removeAll()
        messageToItem.removeAll()
        fileToItem.removeAll()
        earlyEvents.removeAll()
        for url in scopedURLs.values { url.stopAccessingSecurityScopedResource() }
        scopedURLs.removeAll()
        changed(persist: true)
        pump()
    }

    /// Adds files bound for `destination` (nil = Saved Messages). Albums are only formed within one call,
    /// so they never mix destinations.
    public func enqueue(_ files: [PreparedFile], to destination: UploadDestination? = nil) {
        guard !files.isEmpty else { return }
        let destination = destination?.isSavedMessages == true ? nil : destination
        let assigned: [(PreparedFile, UUID?)] = settings.groupAsAlbum
            ? AlbumGrouper.assignGroups(files, sendMediaCompressed: settings.sendMediaCompressed)
            : files.map { ($0, nil) }
        let date = now()
        items.append(contentsOf: assigned.map { UploadItem(file: $0.0, groupId: $0.1, destination: destination, now: date) })
        prune()
        changed(persist: true)
        pump()
    }

    public func cancel(_ id: UUID) async {
        guard let index = index(of: id) else { return }
        switch items[index].state {
        case .queued:
            items[index].state = .cancelled
            items[index].finishedAt = now()
            cleanUpTemporary(items[index])
            changed(persist: true)
        case .uploading:
            let messageId = items[index].messageId
            let destination = items[index].destination
            if let messageId { messageToItem[messageId] = nil }
            items[index].state = .cancelled
            items[index].finishedAt = now()
            complete(id)
            // If the send is still in flight (no message id yet), `run` cancels it when it returns.
            if let messageId { await transport.cancel(messageIds: [messageId], in: destination) }
        case .done, .failed, .cancelled:
            break
        }
    }

    public func retry(_ id: UUID) {
        guard let index = index(of: id), case .failed = items[index].state else { return }
        items[index].state = .queued
        items[index].attempts = 0
        items[index].groupId = nil
        items[index].notBefore = nil
        items[index].finishedAt = nil
        changed(persist: true)
        pump()
    }

    public func remove(_ id: UUID) {
        guard let index = index(of: id), items[index].state.isTerminal else { return }
        cleanUpTemporary(items[index], force: true)
        items.remove(at: index)
        changed(persist: true)
    }

    /// Removes finished (done or cancelled) items from the list.
    public func clearFinished() {
        items.removeAll { item in
            switch item.state {
            case .done, .cancelled:
                cleanUpTemporary(item, force: true)
                return true
            default:
                return false
            }
        }
        changed(persist: true)
    }

    public func handle(_ event: TelegramEvent) {
        switch event {
        case let .fileProgress(fileId, uploaded, total):
            guard let id = fileToItem[fileId], let index = index(of: id), case .uploading = items[index].state else { return }
            let fraction = total > 0 ? min(0.999, Double(uploaded) / Double(total)) : 0
            items[index].state = .uploading(progress: fraction)
            changed(persist: false)

        case let .sendSucceeded(oldId, newId):
            guard let id = messageToItem.removeValue(forKey: oldId) else {
                rememberEarly(event, for: oldId)
                return
            }
            guard let index = index(of: id), case .uploading = items[index].state else { return }
            items[index].state = .done(messageId: newId)
            items[index].messageId = newId
            items[index].finishedAt = now()
            complete(id)

        case let .sendFailed(oldId, error):
            guard let id = messageToItem.removeValue(forKey: oldId) else {
                rememberEarly(event, for: oldId)
                return
            }
            fail(id, error: error)

        case .auth, .connection:
            break
        }
    }

    // MARK: - Scheduling

    private func pump() {
        defer {
            updateActivity()
            scheduleWake()
        }
        guard !isPaused else { return }
        let current = now()
        var started = false
        while activeJobs.count < max(1, settings.maxConcurrent) {
            func isReady(_ item: UploadItem) -> Bool {
                item.state == .queued && (item.notBefore ?? .distantPast) <= current
            }
            guard let first = items.firstIndex(where: isReady) else { break }
            var batch = [first]
            if let group = items[first].groupId {
                batch = items.indices.filter { items[$0].groupId == group && isReady(items[$0]) }
            }
            let jobId = UUID()
            for i in batch {
                items[i].state = .uploading(progress: 0)
                items[i].attempts += 1
                items[i].notBefore = nil
                items[i].messageId = nil
                itemJob[items[i].id] = jobId
            }
            let ids = batch.map { items[$0].id }
            activeJobs[jobId] = Set(ids)
            started = true
            Task { await self.run(ids) }
        }
        if started { changed(persist: true) }
    }

    private func run(_ ids: [UUID]) async {
        let runGeneration = generation
        let transport = self.transport
        var prepared: [(UUID, OutgoingFile)] = []
        // Items of one job share a destination: albums come from a single enqueue call.
        let destination = ids.first.flatMap { index(of: $0) }.flatMap { items[$0].destination }
        for id in ids {
            guard let index = index(of: id) else { continue }
            let item = items[index]
            guard let url = resolveURL(for: item) else {
                fail(id, error: TelegramError(code: 404, message: "Dosya bulunamadı: \(item.displayName)"), retryable: false)
                continue
            }
            let kind = MediaClassifier.kind(forPath: url.path, size: item.size, sendMediaCompressed: settings.sendMediaCompressed)
            let dimensions = kind == .document ? nil : await probe?(url.path, kind)
            let caption = settings.caption.render(fileName: item.displayName, size: item.size, date: item.createdAt)
            prepared.append((id, OutgoingFile(path: url.path, kind: kind, caption: caption, dimensions: dimensions)))
        }

        guard runGeneration == generation else { return }
        // Items may have been cancelled while probing.
        prepared = prepared.filter { isUploading($0.0) }
        guard !prepared.isEmpty else { return }

        do {
            let sent = try await transport.send(prepared.map(\.1), to: destination)
            guard runGeneration == generation else { return }
            var toCancel: [Int64] = []
            for ((id, _), message) in zip(prepared, sent) {
                guard let index = index(of: id), case .uploading = items[index].state else {
                    toCancel.append(message.messageId)
                    continue
                }
                items[index].messageId = message.messageId
                messageToItem[message.messageId] = id
                if let fileId = message.fileId { fileToItem[fileId] = id }
                if let early = earlyEvents.removeValue(forKey: message.messageId) {
                    handle(early)
                }
            }
            changed(persist: true)
            if !toCancel.isEmpty { await transport.cancel(messageIds: toCancel, in: destination) }
        } catch {
            guard runGeneration == generation else { return }
            for (id, _) in prepared where isUploading(id) {
                fail(id, error: error)
            }
        }
    }

    private func fail(_ id: UUID, error: Error, retryable: Bool = true) {
        guard let index = index(of: id), case .uploading = items[index].state else { return }
        if let messageId = items[index].messageId { messageToItem[messageId] = nil }
        items[index].messageId = nil
        if retryable, let delay = retryPolicy.delay(after: error, attempts: items[index].attempts) {
            items[index].state = .queued
            items[index].notBefore = now().addingTimeInterval(delay)
            items[index].groupId = nil
        } else {
            items[index].state = .failed(reason: (error as? TelegramError)?.message ?? error.localizedDescription)
            items[index].finishedAt = now()
        }
        complete(id)
    }

    /// Releases an item from its job and, when the job is empty, starts the next one.
    private func complete(_ id: UUID) {
        fileToItem = fileToItem.filter { $0.value != id }
        if let url = scopedURLs.removeValue(forKey: id) {
            url.stopAccessingSecurityScopedResource()
        }
        if let index = index(of: id) {
            cleanUpTemporary(items[index])
        }
        if let job = itemJob.removeValue(forKey: id) {
            activeJobs[job]?.remove(id)
            if activeJobs[job]?.isEmpty == true { activeJobs[job] = nil }
        }
        prune()
        changed(persist: true)
        pump()
    }

    private func scheduleWake() {
        wakeTask?.cancel()
        wakeTask = nil
        // With every slot busy, a finishing job calls `pump`; waking now would only spin.
        guard !isPaused, activeJobs.count < max(1, settings.maxConcurrent) else { return }
        let current = now()
        // Include items already due: time may have passed since `pump` checked them, and skipping
        // them here would leave the retry stuck until some unrelated event.
        let next = items.compactMap { $0.state == .queued ? $0.notBefore : nil }.min()
        guard let next else { return }
        let delay = next.timeIntervalSince(current)
        wakeTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: UInt64(max(0, delay) * 1_000_000_000) + 1_000_000)
            guard !Task.isCancelled else { return }
            await self?.wake()
        }
    }

    private func wake() {
        pump()
    }

    // MARK: - Helpers

    private func index(of id: UUID) -> Int? {
        items.firstIndex { $0.id == id }
    }

    private func isUploading(_ id: UUID) -> Bool {
        guard let index = index(of: id), case .uploading = items[index].state else { return false }
        return true
    }

    private func rememberEarly(_ event: TelegramEvent, for messageId: Int64) {
        if earlyEvents.count > 200 { earlyEvents.removeAll() }
        earlyEvents[messageId] = event
    }

    private func resolveURL(for item: UploadItem) -> URL? {
        if let bookmark = item.bookmark {
            var stale = false
            if let url = try? URL(resolvingBookmarkData: bookmark, options: .withSecurityScope, relativeTo: nil, bookmarkDataIsStale: &stale) {
                if url.startAccessingSecurityScopedResource() { scopedURLs[item.id] = url }
                if FileManager.default.fileExists(atPath: url.path) { return url }
            } else if let url = try? URL(resolvingBookmarkData: bookmark, options: [], relativeTo: nil, bookmarkDataIsStale: &stale),
                      FileManager.default.fileExists(atPath: url.path) {
                return url
            }
        }
        let url = URL(fileURLWithPath: item.path)
        return FileManager.default.fileExists(atPath: url.path) ? url : nil
    }

    private func cleanUpTemporary(_ item: UploadItem, force: Bool = false) {
        guard item.isTemporary else { return }
        let finishedForGood: Bool
        switch item.state {
        case .done, .cancelled: finishedForGood = true
        default: finishedForGood = force
        }
        guard finishedForGood else { return }
        // Temporary files live in their own staging subfolder.
        try? FileManager.default.removeItem(at: URL(fileURLWithPath: item.path).deletingLastPathComponent())
    }

    private func prune() {
        let cutoff = now().addingTimeInterval(-Self.keepFinishedInterval)
        let finished = items.filter { if case .done = $0.state { return true }; return $0.state == .cancelled }
        let keep = Set(finished.sorted { ($0.finishedAt ?? .distantPast) > ($1.finishedAt ?? .distantPast) }
            .prefix(Self.keepFinishedCount)
            .filter { ($0.finishedAt ?? .distantFuture) >= cutoff }
            .map(\.id))
        let finishedIds = Set(finished.map(\.id))
        items.removeAll { finishedIds.contains($0.id) && !keep.contains($0.id) }
    }

    private func updateActivity() {
        if !activeJobs.isEmpty, activity == nil {
            activity = ProcessInfo.processInfo.beginActivity(
                options: [.userInitiated, .idleSystemSleepDisabled],
                reason: "Uploading files to Telegram"
            )
        } else if activeJobs.isEmpty, let token = activity {
            ProcessInfo.processInfo.endActivity(token)
            activity = nil
        }
    }

    private func changed(persist: Bool) {
        observer?(items)
        if persist { store?.save(items) }
    }
}
