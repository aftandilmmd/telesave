import AppKit
import Network
import Observation
import TeleSaveCore
import TeleSaveTDLib
@preconcurrency import UserNotifications

@MainActor
@Observable
final class AppModel {
    enum Phase: Equatable {
        case needsCredentials
        case running
    }

    enum StatusIcon: Equatable {
        case loggedOut
        case idle
        case uploading(progress: Double)
        case error
    }

    // MARK: Observable state

    private(set) var phase: Phase = .needsCredentials
    private(set) var auth: AuthState = .initializing
    private(set) var connection: ConnectionState = .connecting
    private(set) var me: TelegramUser?
    /// Profile photo of the logged-in user; nil → initials are shown.
    private(set) var avatar: NSImage?
    private(set) var items: [UploadItem] = []
    private(set) var bytesPerSecond: Double = 0
    private(set) var networkAvailable = true
    /// Announcement from the remote config.
    private(set) var remoteMessage: String?
    /// Remote config says this version is too old.
    private(set) var updateRequired = false
    /// Newer release on GitHub, if any.
    private(set) var availableUpdate: ReleaseInfo?
    private var updateTask: Task<Void, Never>?

    var appVersion: String {
        Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "dev"
    }
    /// Where drops currently go. Saved Messages unless the user picked another chat.
    private(set) var destination: UploadDestination = .savedMessages
    private(set) var recentDestinations: [UploadDestination] = []
    private(set) var chats: [ChatSummary] = []
    private(set) var isLoadingChats = false
    var destinationError: String?
    /// Items whose file is being downloaded back from Telegram / deleted there.
    private(set) var busyItems: Set<UUID> = []

    // "Dosyalar" tab: every file in the destination chat.
    private(set) var chatFiles: [ChatFile] = []
    private(set) var chatFilesLoading = false
    private(set) var chatFilesComplete = false
    private(set) var chatFilesError: String?
    private(set) var busyChatFiles: Set<Int64> = []
    /// Messages sent by TeleSave from this Mac (shown with a badge in the chat file list).
    private(set) var sentRegistry = SentMessageRegistry.load(from: AppPaths.sentMessages)
    private var chatFilesCursor: ChatFileCursor?
    /// Bumped on every reload so results of an older search are dropped.
    private var chatFilesGeneration = 0
    private var chatFilesQuery = ""
    private var chatFilesFilter: ChatFileFilter = .all
    private var chatFilesRefreshTask: Task<Void, Never>?
    private static let chatFilesPageSize = 40
    var authError: String?
    var notice: String?
    var isBusy = false
    var settings: AppSettings {
        didSet {
            guard settings != oldValue else { return }
            settings.save()
            L10n.selectedCode = settings.language
            if settings.shortcuts != oldValue.shortcuts { applyShortcuts() }
            if !AppSettings.isAppStoreBuild, settings.finderServiceEnabled != oldValue.finderServiceEnabled {
                FinderService.setEnabled(settings.finderServiceEnabled)
            }
            let queueSettings = settings.queueSettings
            Task { await queue?.update(settings: queueSettings) }
        }
    }

    /// Asks the status item to show the popover (e.g. after a drop on the icon).
    var onRequestPopover: (() -> Void)?

    // MARK: Private

    private let keychain = KeychainStore(service: "com.telesave.app")
    private let intake = DropIntake(stagingDirectory: AppPaths.staging)
    private var client: TDLibTelegramClient?
    private var queue: UploadQueue?
    private var eventTask: Task<Void, Never>?
    private var activeCredentials: ApiCredentials?
    private let remoteConfig: RemoteConfigService?
    private var configTask: Task<Void, Never>?
    private static let configRefreshInterval: UInt64 = 6 * 60 * 60 * 1_000_000_000
    private let pathMonitor = NWPathMonitor()
    private var rate = TransferRate()
    private var lastSnapshot = 0
    private var previousStates: [UUID: UploadItem.State] = [:]
    private var pendingDone = 0
    private var pendingFailed: [String] = []
    private var notifyTask: Task<Void, Never>?
    private var noticeTask: Task<Void, Never>?

    private static let encryptionKeyAccount = "tdlib-db-key"

    #if DEBUG
    /// Sample state for README screenshots: no Telegram connection, nothing persisted.
    init(demoUser: TelegramUser, demoItems: [UploadItem], language: String) {
        var demo = AppSettings()
        demo.showChatFiles = false
        demo.language = language
        settings = demo
        remoteConfig = nil
        L10n.selectedCode = language
        phase = .running
        auth = .ready
        connection = .ready
        me = demoUser
        items = demoItems
        bytesPerSecond = 3_400_000
        chats = [
            ChatSummary(id: 1, title: "Family", kind: .group, isForum: false, username: nil),
            ChatSummary(id: 2, title: "Design Team", kind: .group, isForum: true, username: nil),
            ChatSummary(id: 3, title: "Project Updates", kind: .channel, isForum: false, username: "projectupdates"),
            ChatSummary(id: 4, title: "Elvin", kind: .user, isForum: false, username: "elvin"),
            ChatSummary(id: 5, title: "Photo Archive", kind: .channel, isForum: false, username: nil),
        ]
        recentDestinations = [UploadDestination(chatId: 1, title: "Family")]
    }
    #endif

    init() {
        let loaded = AppSettings.load()
        L10n.selectedCode = loaded.language
        settings = loaded
        remoteConfig = Self.makeRemoteConfigService(keychain: keychain)
        startNetworkMonitor()
        let cached = remoteConfig?.initial
        applyRemoteNotices(cached)
        if let credentials = CredentialsResolver.resolve(keychain: keychain, remote: cached?.credentials) {
            start(with: credentials)
        }
        startRemoteConfigPolling()
        startUpdateChecks()
    }

    /// Checks GitHub releases at launch and then daily.
    private func startUpdateChecks() {
        // App Store builds are updated by the App Store; pointing users elsewhere is not allowed.
        guard !AppSettings.isAppStoreBuild else { return }
        updateTask = Task { [weak self] in
            while !Task.isCancelled {
                let release = await UpdateChecker.latestRelease()
                guard let self else { return }
                if let release {
                    self.availableUpdate = release.isNewer(than: self.appVersion) ? release : nil
                }
                try? await Task.sleep(nanoseconds: 24 * 60 * 60 * 1_000_000_000)
            }
        }
    }

    // MARK: - Lifecycle

    /// Starts a TDLib client. The upload queue is created once and survives client restarts.
    private func start(with credentials: ApiCredentials) {
        phase = .running
        auth = .initializing
        activeCredentials = credentials

        let (stream, continuation) = AsyncStream.makeStream(of: TelegramEvent.self)
        let configuration = TDLibTelegramClient.Configuration(
            databaseDirectory: AppPaths.database,
            filesDirectory: AppPaths.files,
            logFile: AppPaths.log,
            credentials: credentials,
            encryptionKey: databaseKey(),
            applicationVersion: Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "dev"
        )
        let client = TDLibTelegramClient(configuration: configuration) { event in
            continuation.yield(event)
        }
        self.client = client
        if let queue {
            Task { await queue.replaceTransport(client) }
        } else {
            let queue = UploadQueue(
                transport: client,
                store: QueueStore(fileURL: AppPaths.queue),
                settings: settings.queueSettings,
                probe: { path, kind in await MediaProbe.dimensions(path: path, kind: kind) }
            )
            self.queue = queue
            let sequencer = SnapshotSequencer()
            let observer: UploadQueue.Observer = { [weak self] items in
                let seq = sequencer.next()
                Task { @MainActor in self?.apply(items, sequence: seq) }
            }
            Task { await queue.setObserver(observer) }
        }

        // Single consumer keeps TDLib event order intact.
        eventTask = Task { [weak self] in
            for await event in stream {
                await self?.handle(event)
            }
        }
        client.start()
    }

    func shutdown() {
        configTask?.cancel()
        updateTask?.cancel()
        client?.close()
    }

    /// Closes the current TDLib client (off the main thread) and stops forwarding its events.
    /// The next client must not open the database until this one has closed.
    private func stopClient() async {
        eventTask?.cancel()
        eventTask = nil
        let old = client
        client = nil
        activeCredentials = nil
        me = nil
        auth = .initializing
        updateQueuePause()
        if let old {
            await Task.detached { old.close() }.value
        }
    }

    private func restart(with credentials: ApiCredentials) async {
        await stopClient()
        start(with: credentials)
    }

    // MARK: - Remote config

    private static func makeRemoteConfigService(keychain: KeychainStore) -> RemoteConfigService? {
        let urls = RemoteConfigService.parseURLs(Bundle.main.object(forInfoDictionaryKey: "TSConfigURLs") as? String)
        guard !urls.isEmpty,
              let key = Bundle.main.object(forInfoDictionaryKey: "TSConfigPublicKey") as? String,
              let verifier = RemoteConfigVerifier(publicKeyBase64: key) else { return nil }
        return RemoteConfigService(urls: urls, verifier: verifier, keychain: keychain)
    }

    private func startRemoteConfigPolling() {
        guard let remoteConfig else { return }
        configTask = Task { [weak self] in
            while !Task.isCancelled {
                if let config = await remoteConfig.refresh() {
                    await self?.applyRemote(config)
                }
                try? await Task.sleep(nanoseconds: Self.configRefreshInterval)
            }
        }
    }

    private func applyRemote(_ config: RemoteConfig) async {
        applyRemoteNotices(config)
        guard let credentials = CredentialsResolver.resolve(keychain: keychain, remote: config.credentials),
              credentials != activeCredentials else { return }
        if client == nil {
            start(with: credentials)
        } else {
            // API key rotated remotely: reconnect with the new key. Uploads resume on the new client.
            await restart(with: credentials)
        }
    }

    private func applyRemoteNotices(_ config: RemoteConfig?) {
        remoteMessage = config?.message.flatMap { $0.isEmpty ? nil : $0 }
        let version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0"
        updateRequired = config?.requiresUpdate(currentVersion: version) ?? false
    }

    private func databaseKey() -> Data {
        if let key = keychain.data(for: Self.encryptionKeyAccount), key.count == 32 { return key }
        var bytes = [UInt8](repeating: 0, count: 32)
        _ = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
        let key = Data(bytes)
        keychain.set(key, for: Self.encryptionKeyAccount)
        return key
    }

    private func startNetworkMonitor() {
        pathMonitor.pathUpdateHandler = { [weak self] path in
            let available = path.status == .satisfied
            Task { @MainActor in
                self?.networkAvailable = available
                self?.updateQueuePause()
            }
        }
        pathMonitor.start(queue: DispatchQueue(label: "TeleSave.network"))
    }

    // MARK: - Events

    private func handle(_ event: TelegramEvent) async {
        switch event {
        case .auth(let state):
            if state != auth { authError = nil }
            auth = state
            if state.isReady {
                await loadMe()
            } else {
                me = nil
                avatar = nil
            }
            updateQueuePause()
        case .connection(let state):
            connection = state
        default:
            await queue?.handle(event)
        }
    }

    private func loadMe() async {
        guard let client else { return }
        do {
            let user = try await client.fetchMe()
            if user.id != me?.id { restoreDestinations(for: user.id) }
            me = user
            recordSent(items)
            await loadAvatar(for: user)
        } catch {
            showNotice(L("Hesap bilgisi alınamadı: %@", error.localizedDescription))
        }
    }

    private func loadAvatar(for user: TelegramUser) async {
        guard let client, let fileId = user.photoFileId else {
            avatar = nil
            return
        }
        // Not critical: on failure the initials stay.
        if let path = try? await client.downloadFile(fileId), me?.id == user.id {
            avatar = NSImage(contentsOfFile: path)
        }
    }

    private func updateQueuePause() {
        let paused = !(auth.isReady && me != nil && networkAvailable)
        Task { await queue?.setPaused(paused) }
    }

    private func apply(_ snapshot: [UploadItem], sequence: Int) {
        guard sequence > lastSnapshot else { return }
        lastSnapshot = sequence
        items = snapshot

        let inFlight = snapshot.filter { if case .uploading = $0.state { return true }; return false }
        if inFlight.isEmpty {
            rate.reset()
        } else {
            rate.sample(uploadedBytes: inFlight.reduce(0) { $0 + $1.progress * Double($1.size) }, at: Date())
        }
        bytesPerSecond = rate.bytesPerSecond

        for item in snapshot {
            let before = previousStates[item.id]
            guard let before, !before.isTerminal else { continue }
            switch item.state {
            case .done: pendingDone += 1
            case .failed: pendingFailed.append(item.displayName)
            default: break
            }
        }
        recordSent(snapshot)
        let newlyDone = snapshot.contains { item in
            guard case .done = item.state, let before = previousStates[item.id] else { return false }
            return !before.isTerminal
        }
        previousStates = Dictionary(uniqueKeysWithValues: snapshot.map { ($0.id, $0.state) })
        if newlyDone && settings.showChatFiles { scheduleChatFilesRefresh() }
        if pendingDone > 0 || !pendingFailed.isEmpty { scheduleNotification() }
    }

    // MARK: - Notifications

    private func scheduleNotification() {
        notifyTask?.cancel()
        notifyTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 1_500_000_000)
            guard !Task.isCancelled else { return }
            self?.postNotification()
        }
    }

    private func postNotification() {
        let done = pendingDone
        let failed = pendingFailed
        pendingDone = 0
        pendingFailed = []
        guard settings.notificationsEnabled, Bundle.main.bundleIdentifier != nil else { return }
        let content = UNMutableNotificationContent()
        content.title = "TeleSave"
        var lines: [String] = []
        if done > 0 { lines.append(L("%@ dosya yüklendi.", String(done))) }
        if !failed.isEmpty { lines.append(L("%@ dosya yüklenemedi: %@", String(failed.count), failed.prefix(3).joined(separator: ", "))) }
        content.body = lines.joined(separator: "\n")
        let request = UNNotificationRequest(identifier: UUID().uuidString, content: content, trigger: nil)
        Task {
            let center = UNUserNotificationCenter.current()
            guard (try? await center.requestAuthorization(options: [.alert, .sound])) == true else { return }
            try? await center.add(request)
        }
    }

    func showNotice(_ text: String) {
        notice = text
        noticeTask?.cancel()
        noticeTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 6_000_000_000)
            guard !Task.isCancelled else { return }
            self?.notice = nil
        }
    }

    // MARK: - Status

    var statusIcon: StatusIcon {
        guard phase == .running, auth.isReady else { return .loggedOut }
        let active = items.filter { if case .uploading = $0.state { return true }; return $0.state == .queued }
        if !active.isEmpty {
            let total = active.reduce(0.0) { $0 + Double($1.size) }
            let done = active.reduce(0.0) { $0 + $1.progress * Double($1.size) }
            return .uploading(progress: total > 0 ? done / total : 0)
        }
        if items.contains(where: { if case .failed = $0.state { return true }; return false }) { return .error }
        return .idle
    }

    var remainingSeconds: TimeInterval? {
        let remaining = items.reduce(0.0) { sum, item in
            switch item.state {
            case .queued, .uploading: return sum + (1 - item.progress) * Double(item.size)
            default: return sum
            }
        }
        return rate.eta(remainingBytes: remaining)
    }

    var canUpload: Bool { auth.isReady && me != nil }

    /// Builds with a baked-in API key never ask users for one.
    var hasBundledCredentials: Bool { CredentialsResolver.bundled() != nil }

    // MARK: - Uploads

    func handleDrop(_ urls: [URL], fromStatusItem: Bool = false, fromShortcut: Bool = false, temporary: Bool = false) {
        guard !urls.isEmpty else { return }
        guard canUpload, let me, let queue else {
            showNotice(L("Yüklemek için önce Telegram'a giriş yapın."))
            onRequestPopover?()
            return
        }
        Task {
            let result = await intake.prepare(urls, maxFileSize: me.maxUploadSize, folderMode: settings.folderMode, temporary: temporary)
            await queue.enqueue(result.accepted, to: destination)
            if !result.rejected.isEmpty {
                showNotice(result.rejected.map(\.message).joined(separator: "\n"))
            }
            // Always show the panel when files go somewhere other than Saved Messages, so the target is visible.
            let attention = !result.rejected.isEmpty || !destination.isSavedMessages
            if (fromStatusItem && (settings.openPopoverOnDrop || attention)) || (fromShortcut && attention) {
                onRequestPopover?()
            }
        }
    }

    // MARK: - Global shortcuts

    /// Actions whose shortcut another app already holds; shown in Settings.
    private(set) var failedShortcuts: Set<ShortcutAction> = []
    /// Toggles the panel (set by the status item).
    var onTogglePopover: (() -> Void)?

    func applyShortcuts() {
        failedShortcuts = GlobalShortcuts.shared.apply(settings.shortcuts)
    }

    func shortcutsChanged() {
        failedShortcuts = GlobalShortcuts.shared.failed
    }

    func perform(_ action: ShortcutAction) {
        switch action {
        case .uploadClipboard: uploadClipboardFromShortcut()
        case .togglePanel: onTogglePopover?()
        }
    }

    /// Warning shown under a shortcut in Settings, if it will not work as expected.
    func shortcutConflict(_ action: ShortcutAction) -> String? {
        guard let shortcut = settings.shortcuts[action] else { return nil }
        if settings.uploadShortcut != nil && settings.uploadShortcut == settings.panelShortcut {
            return L("İki işlem aynı kısayolu kullanıyor.")
        }
        if failedShortcuts.contains(action) { return L("Bu kısayol başka bir uygulama tarafından kullanılıyor.") }
        if shortcut.isUsedBySystem { return L("macOS da bu kısayolu kullanıyor.") }
        return nil
    }

    /// Shortcut: uploads copied files (or a copied image) without opening the panel. The panel only opens when
    /// there is something to see: not signed in, nothing to send, a rejected file, or a target other than Saved Messages.
    func uploadClipboardFromShortcut() {
        guard canUpload else {
            showNotice(L("Yüklemek için önce Telegram'a giriş yapın."))
            onRequestPopover?()
            return
        }
        if !pasteFromClipboard(viaShortcut: true) { onRequestPopover?() }
    }

    /// Sends what is on the clipboard: files copied in Finder, or an image (e.g. a screenshot copied with ⌃⇧⌘4).
    /// Returns false when there was nothing to send.
    @discardableResult
    func pasteFromClipboard(viaShortcut: Bool = false) -> Bool {
        let pasteboard = NSPasteboard.general
        if let urls = pasteboard.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL],
           !urls.isEmpty {
            handleDrop(urls, fromShortcut: viaShortcut)
            return true
        }
        guard let png = Self.pngData(from: pasteboard) else {
            showNotice(L("Panoda gönderilecek dosya ya da görsel yok."))
            return false
        }
        let formatter = DateFormatter()
        formatter.dateFormat = "yyyy-MM-dd 'at' HH.mm.ss"
        // Own staging subfolder: temporary files are cleaned up together with their folder.
        let folder = AppPaths.staging.appendingPathComponent(UUID().uuidString, isDirectory: true)
        let file = folder.appendingPathComponent(L("Yapıştırılan görsel %@.png", formatter.string(from: Date())))
        do {
            try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
            try png.write(to: file)
            handleDrop([file], fromShortcut: viaShortcut, temporary: true)
            return true
        } catch {
            showNotice(L("Görsel kaydedilemedi: %@", error.localizedDescription))
            return false
        }
    }

    private static func pngData(from pasteboard: NSPasteboard) -> Data? {
        if let png = pasteboard.data(forType: .png) { return png }
        guard let image = NSImage(pasteboard: pasteboard),
              let tiff = image.tiffRepresentation,
              let rep = NSBitmapImageRep(data: tiff) else { return nil }
        return rep.representation(using: .png, properties: [:])
    }

    func pickFiles() {
        let panel = NSOpenPanel()
        panel.canChooseFiles = true
        panel.canChooseDirectories = true
        panel.allowsMultipleSelection = true
        panel.prompt = L("Gönder")
        panel.message = L("Kayıtlı Mesajlara yüklenecek dosyaları seçin")
        NSApp.activate(ignoringOtherApps: true)
        if panel.runModal() == .OK {
            handleDrop(panel.urls)
        }
    }

    /// Downloads the sent file back from Telegram into ~/Downloads and reveals it in Finder.
    func download(_ item: UploadItem) {
        guard case .done(let messageId) = item.state, let client, !busyItems.contains(item.id) else { return }
        busyItems.insert(item.id)
        Task {
            defer { busyItems.remove(item.id) }
            do {
                let path = try await client.downloadMessageFile(messageId: messageId, in: item.destination)
                let downloads = FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask)[0]
                let target = MessageFileLocator.uniqueURL(in: downloads, fileName: item.displayName)
                try FileManager.default.copyItem(at: URL(fileURLWithPath: path), to: target)
                NSWorkspace.shared.activateFileViewerSelecting([target])
            } catch {
                showNotice(L("%@ indirilemedi: %@", item.displayName, error.localizedDescription))
            }
        }
    }

    /// Deletes the sent message from Telegram (for everyone) and removes the row.
    func deleteFromTelegram(_ item: UploadItem) {
        guard case .done(let messageId) = item.state, let client, !busyItems.contains(item.id) else { return }
        busyItems.insert(item.id)
        Task {
            defer { busyItems.remove(item.id) }
            do {
                try await client.deleteMessages([messageId], in: item.destination)
                forgetSent(chatId: item.destination?.chatId, messageId: messageId)
                await queue?.remove(item.id)
                chatFiles.removeAll { $0.messageId == messageId }
                showNotice(L("%@ Telegram'dan silindi.", item.displayName))
            } catch {
                showNotice(L("%@ silinemedi: %@", item.displayName, error.localizedDescription))
            }
        }
    }

    func cancel(_ item: UploadItem) { Task { await queue?.cancel(item.id) } }
    func retry(_ item: UploadItem) { Task { await queue?.retry(item.id) } }
    func remove(_ item: UploadItem) { Task { await queue?.remove(item.id) } }
    func clearFinished() { Task { await queue?.clearFinished() } }

    func openInTelegram(_ target: UploadDestination? = nil) {
        guard let me else { return }
        let deepLink: String
        if let target, !target.isSavedMessages {
            // Chats without a public username have no stable deep link; just bring Telegram up.
            deepLink = target.username.map { "tg://resolve?domain=\($0)" } ?? "tg://"
        } else {
            deepLink = me.username.map { "tg://resolve?domain=\($0)" } ?? "tg://openmessage?user_id=\(me.id)"
        }
        if let url = URL(string: deepLink), NSWorkspace.shared.urlForApplication(toOpen: url) != nil {
            NSWorkspace.shared.open(url)
        } else if let web = URL(string: "https://web.telegram.org/") {
            NSWorkspace.shared.open(web)
        }
    }

    // MARK: - Sent-message registry

    /// Adds finished uploads to the permanent registry (queue entries are pruned after a day).
    private func recordSent(_ snapshot: [UploadItem]) {
        guard let me else { return }
        var changed = false
        for item in snapshot {
            guard case .done(let messageId) = item.state else { continue }
            changed = sentRegistry.insert(owner: me.id, chatId: item.destination?.chatId, messageId: messageId) || changed
        }
        if changed { sentRegistry.save(to: AppPaths.sentMessages) }
    }

    private func forgetSent(chatId: Int64?, messageId: Int64) {
        guard let me, sentRegistry.remove(owner: me.id, chatId: chatId, messageId: messageId) else { return }
        sentRegistry.save(to: AppPaths.sentMessages)
    }

    /// Whether a file in the shown chat was uploaded by TeleSave from this Mac.
    func isFromTeleSave(_ file: ChatFile) -> Bool {
        guard let me else { return false }
        return sentRegistry.contains(owner: me.id, chatId: destination.chatId, messageId: file.messageId)
    }

    // MARK: - Chat files

    /// Starts a new listing (first page) for the destination chat.
    func reloadChatFiles(query: String, filter: ChatFileFilter) async {
        chatFilesGeneration += 1
        chatFilesQuery = query
        chatFilesFilter = filter
        chatFiles = []
        chatFilesCursor = nil
        chatFilesComplete = false
        chatFilesError = nil
        chatFilesLoading = false
        await loadMoreChatFiles()
    }

    func loadMoreChatFiles() async {
        guard let client, !chatFilesLoading, !chatFilesComplete else { return }
        let generation = chatFilesGeneration
        chatFilesLoading = true
        do {
            // A search may scan several pages before anything matches; keep going a while so an empty
            // first page does not look like "no results". After that the user can continue manually.
            var added = 0
            var rounds = 0
            repeat {
                let page = try await client.searchChatFiles(
                    in: destination.isSavedMessages ? nil : destination,
                    query: chatFilesQuery, filter: chatFilesFilter,
                    cursor: chatFilesCursor, limit: Self.chatFilesPageSize
                )
                guard generation == chatFilesGeneration else { return }
                let known = Set(chatFiles.map(\.id))
                let fresh = page.files.filter { !known.contains($0.id) }
                chatFiles.append(contentsOf: fresh)
                added += fresh.count
                chatFilesCursor = page.cursor
                chatFilesComplete = page.isLastPage
                rounds += 1
            } while added == 0 && !chatFilesComplete && rounds < 6
        } catch {
            guard generation == chatFilesGeneration else { return }
            chatFilesError = L("Dosyalar yüklenemedi: %@", error.localizedDescription)
        }
        if generation == chatFilesGeneration { chatFilesLoading = false }
    }

    /// New uploads finished: refresh the list shortly (debounced).
    private func scheduleChatFilesRefresh() {
        chatFilesRefreshTask?.cancel()
        chatFilesRefreshTask = Task { [weak self] in
            try? await Task.sleep(nanoseconds: 1_000_000_000)
            guard !Task.isCancelled, let self else { return }
            await self.reloadChatFiles(query: self.chatFilesQuery, filter: self.chatFilesFilter)
        }
    }

    /// Path of a small preview for a chat file (downloads it on first use).
    func thumbnailPath(for file: ChatFile) async -> String? {
        guard let client, let id = file.thumbnailFileId else { return nil }
        return try? await client.downloadFile(id)
    }

    func download(_ file: ChatFile) {
        guard let client, !busyChatFiles.contains(file.id) else { return }
        busyChatFiles.insert(file.id)
        Task {
            defer { busyChatFiles.remove(file.id) }
            do {
                let path = try await client.downloadFile(file.fileId)
                let downloads = FileManager.default.urls(for: .downloadsDirectory, in: .userDomainMask)[0]
                let target = MessageFileLocator.uniqueURL(in: downloads, fileName: file.fileName)
                try FileManager.default.copyItem(at: URL(fileURLWithPath: path), to: target)
                NSWorkspace.shared.activateFileViewerSelecting([target])
            } catch {
                showNotice(L("%@ indirilemedi: %@", file.fileName, error.localizedDescription))
            }
        }
    }

    /// Saved Messages: everything is yours. Elsewhere only your own messages are offered for deletion.
    func canDelete(_ file: ChatFile) -> Bool {
        destination.isSavedMessages || file.isOutgoing
    }

    func delete(_ file: ChatFile) {
        guard let client, !busyChatFiles.contains(file.id) else { return }
        busyChatFiles.insert(file.id)
        let target = destination.isSavedMessages ? nil : destination
        Task {
            defer { busyChatFiles.remove(file.id) }
            do {
                try await client.deleteMessages([file.messageId], in: target)
                chatFiles.removeAll { $0.id == file.id }
                forgetSent(chatId: target?.chatId, messageId: file.messageId)
                // The same message may also be in the uploads list.
                if let item = items.first(where: { if case .done(let id) = $0.state { return id == file.messageId }; return false }) {
                    await queue?.remove(item.id)
                }
            } catch {
                showNotice(L("%@ silinemedi: %@", file.fileName, error.localizedDescription))
            }
        }
    }

    // MARK: - Destinations

    private struct StoredDestinations: Codable {
        var ownerId: Int64
        var current: UploadDestination
        var recents: [UploadDestination]
    }

    private static let destinationsKey = "destinations"

    /// Destinations belong to an account; a different login starts again from Saved Messages.
    private func restoreDestinations(for userId: Int64) {
        chats = []
        chatFilesGeneration += 1
        chatFiles = []
        guard let data = UserDefaults.standard.data(forKey: Self.destinationsKey),
              let stored = try? JSONDecoder().decode(StoredDestinations.self, from: data),
              stored.ownerId == userId else {
            destination = .savedMessages
            recentDestinations = []
            return
        }
        destination = stored.current
        recentDestinations = stored.recents
    }

    private func saveDestinations() {
        guard let me else { return }
        let stored = StoredDestinations(ownerId: me.id, current: destination, recents: recentDestinations)
        if let data = try? JSONEncoder().encode(stored) {
            UserDefaults.standard.set(data, forKey: Self.destinationsKey)
        }
    }

    func refreshChats() async {
        guard let client, !isLoadingChats else { return }
        isLoadingChats = true
        defer { isLoadingChats = false }
        do {
            chats = try await client.loadChats(limit: 100).filter { $0.kind != .savedMessages }
        } catch {
            destinationError = L("Sohbetler yüklenemedi: %@", error.localizedDescription)
        }
    }

    func forumTopics(for chat: ChatSummary) async -> [ForumTopicSummary] {
        guard let client else { return [] }
        do {
            return try await client.forumTopics(in: chat.id).filter { !$0.isClosed }
        } catch {
            destinationError = L("Konular yüklenemedi: %@", error.localizedDescription)
            return []
        }
    }

    /// Selects a chat (and forum topic) after checking the user may send files there.
    func choose(_ chat: ChatSummary, topic: ForumTopicSummary? = nil) async -> Bool {
        guard let client else { return false }
        destinationError = nil
        do {
            guard try await client.canSendFiles(to: chat.id) else {
                destinationError = chat.kind == .channel
                    ? L("%@: bu kanalda gönderi yetkiniz yok.", chat.title)
                    : L("%@: bu sohbete dosya gönderme izniniz yok.", chat.title)
                return false
            }
        } catch {
            destinationError = L("İzin kontrol edilemedi: %@", error.localizedDescription)
            return false
        }
        let title = topic.map { "\(chat.title) › \($0.name)" } ?? chat.title
        select(UploadDestination(chatId: chat.id, forumTopicId: topic?.id, title: title, username: chat.username))
        return true
    }

    func select(_ newDestination: UploadDestination) {
        destination = newDestination
        if !newDestination.isSavedMessages {
            recentDestinations.removeAll { $0.chatId == newDestination.chatId && $0.forumTopicId == newDestination.forumTopicId }
            recentDestinations.insert(newDestination, at: 0)
            recentDestinations = Array(recentDestinations.prefix(5))
        }
        saveDestinations()
    }

    // MARK: - Auth actions

    private func perform(_ action: @escaping (TDLibTelegramClient) async throws -> Void) {
        guard let client else { return }
        isBusy = true
        authError = nil
        Task {
            do {
                try await action(client)
            } catch {
                authError = AuthErrorText.describe(error)
            }
            isBusy = false
        }
    }

    func requestQRCode() { perform { try await $0.requestQRCode() } }

    func submitPhone(_ phone: String) {
        let cleaned = phone.filter { $0.isNumber || $0 == "+" }
        perform { try await $0.submitPhoneNumber(cleaned) }
    }

    func submitCode(_ code: String) {
        let cleaned = code.filter(\.isNumber)
        perform { try await $0.submitCode(cleaned) }
    }

    func submitPassword(_ password: String) { perform { try await $0.submitPassword(password) } }

    func logOut() { perform { try await $0.logOut() } }

    // MARK: - Credentials

    /// True while a new API key is being checked with Telegram.
    private(set) var isValidatingKey = false

    /// api_id currently in use (shown on the change-key page).
    var currentApiId: Int? { activeCredentials?.apiId }

    /// Checks the key with Telegram first; only a key Telegram accepts replaces the current one.
    /// On failure nothing changes and the app keeps running with the old key.
    @discardableResult
    func saveCredentials(apiId: String, apiHash: String) async -> Bool {
        let credentials = ApiCredentials(apiId: Int(apiId.trimmingCharacters(in: .whitespaces)) ?? 0,
                                         apiHash: apiHash.trimmingCharacters(in: .whitespaces).lowercased())
        guard credentials.isValid else {
            authError = L("api_id bir sayı, api_hash 32 karakterlik bir hex dizesi olmalı.")
            return false
        }
        authError = nil
        if credentials == activeCredentials { return true }

        isValidatingKey = true
        let outcome = await ApiKeyValidator.validate(credentials)
        isValidatingKey = false
        switch outcome {
        case .invalid:
            authError = L("Bu anahtar Telegram tarafından reddedildi. api_id ve api_hash değerlerini kontrol edin.")
            return false
        case .failed(let reason):
            authError = L("Anahtar doğrulanamadı: %@", reason)
            return false
        case .valid:
            break
        }
        guard CredentialsResolver.save(credentials, keychain: keychain) else {
            authError = L("Anahtar Keychain'e kaydedilemedi.")
            return false
        }
        await restart(with: credentials)
        return true
    }
}

/// Monotonic counter so out-of-order main-actor hops never apply a stale queue snapshot.
private final class SnapshotSequencer: @unchecked Sendable {
    private let lock = NSLock()
    private var value = 0

    func next() -> Int {
        lock.withLock {
            value += 1
            return value
        }
    }
}
