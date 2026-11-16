import Foundation
import TeleSaveCore

/// TelegramClient backed by TDLib's JSON interface. Logs in as the user (MTProto), so it can write to
/// the user's own Saved Messages chat, which the Bot API cannot.
public final class TDLibTelegramClient: TelegramClient, @unchecked Sendable {
    public struct Configuration: Sendable {
        public var databaseDirectory: URL
        public var filesDirectory: URL
        public var logFile: URL?
        public var credentials: ApiCredentials
        public var encryptionKey: Data
        public var applicationVersion: String
        public var useTestDC: Bool

        public init(databaseDirectory: URL, filesDirectory: URL, logFile: URL?, credentials: ApiCredentials, encryptionKey: Data, applicationVersion: String, useTestDC: Bool = false) {
            self.databaseDirectory = databaseDirectory
            self.filesDirectory = filesDirectory
            self.logFile = logFile
            self.credentials = credentials
            self.encryptionKey = encryptionKey
            self.applicationVersion = applicationVersion
            self.useTestDC = useTestDC
        }
    }

    private let configuration: Configuration
    private let eventHandler: @Sendable (TelegramEvent) -> Void
    private let lock = NSLock()
    private var client: TDJSONClient?
    private var savedMessagesChatId: Int64?
    private var restartOnClose = false
    private var closedSignal: DispatchSemaphore?

    public init(configuration: Configuration, eventHandler: @escaping @Sendable (TelegramEvent) -> Void) {
        self.configuration = configuration
        self.eventHandler = eventHandler
    }

    // MARK: - Lifecycle

    public func start() {
        configureLogging()
        makeClient()
    }

    public func close() {
        let semaphore = DispatchSemaphore(value: 0)
        lock.lock()
        closedSignal = semaphore
        restartOnClose = false
        let current = client
        lock.unlock()
        guard let current else { return }
        current.send(["@type": "close"])
        _ = semaphore.wait(timeout: .now() + 3)
    }

    private func makeClient() {
        let newClient = TDJSONClient { [weak self] update in
            self?.handleUpdate(update)
        }
        lock.lock()
        client = newClient
        savedMessagesChatId = nil
        lock.unlock()
        // TDLib only starts emitting updates after it receives the first request.
        newClient.send(["@type": "getOption", "name": "version"])
    }

    private func configureLogging() {
        TDJSONClient.execute(["@type": "setLogVerbosityLevel", "new_verbosity_level": 1])
        if let logFile = configuration.logFile {
            try? FileManager.default.createDirectory(at: logFile.deletingLastPathComponent(), withIntermediateDirectories: true)
            TDJSONClient.execute([
                "@type": "setLogStream",
                "log_stream": [
                    "@type": "logStreamFile",
                    "path": logFile.path,
                    "max_file_size": 5 * 1024 * 1024,
                    "redirect_stderr": false
                ] as TDObject
            ])
        }
    }

    private func currentClient() throws -> TDJSONClient {
        lock.lock(); defer { lock.unlock() }
        guard let client else { throw TelegramError(code: 0, message: "Telegram client not started") }
        return client
    }

    private func request(_ object: TDObject) async throws -> TDObject {
        try await currentClient().request(object)
    }

    // MARK: - Updates

    private func handleUpdate(_ update: TDObject) {
        switch update["@type"] as? String {
        case "updateAuthorizationState":
            guard let state = update.object("authorization_state") else { return }
            let type = state["@type"] as? String
            if type == "authorizationStateWaitTdlibParameters" {
                sendParameters()
                return
            }
            if type == "authorizationStateClosed" {
                handleClosed()
            }
            if let mapped = AuthState(tdObject: state) {
                eventHandler(.auth(mapped))
            }

        case "updateConnectionState":
            if let state = update.object("state"), let mapped = ConnectionState(tdObject: state) {
                eventHandler(.connection(mapped))
            }

        case "updateFile":
            guard let file = update.object("file"), let id = file.int64("id") else { return }
            let size = file.int64("size").flatMap { $0 > 0 ? $0 : nil } ?? file.int64("expected_size") ?? 0
            let uploaded = file.object("remote")?.int64("uploaded_size") ?? 0
            eventHandler(.fileProgress(fileId: Int32(truncatingIfNeeded: id), uploaded: uploaded, total: size))

        case "updateMessageSendSucceeded":
            guard let oldId = update.int64("old_message_id"), let newId = update.object("message")?.int64("id") else { return }
            eventHandler(.sendSucceeded(oldMessageId: oldId, newMessageId: newId))

        case "updateMessageSendFailed":
            guard let oldId = update.int64("old_message_id") else { return }
            let error: TelegramError
            if let object = update.object("error") {
                error = TelegramError(code: Int(object.int64("code") ?? 0), message: object["message"] as? String ?? "Send failed")
            } else {
                error = TelegramError(code: Int(update.int64("error_code") ?? 0), message: update["error_message"] as? String ?? "Send failed")
            }
            eventHandler(.sendFailed(oldMessageId: oldId, error: error))

        default:
            break
        }
    }

    private func sendParameters() {
        let fm = FileManager.default
        try? fm.createDirectory(at: configuration.databaseDirectory, withIntermediateDirectories: true)
        try? fm.createDirectory(at: configuration.filesDirectory, withIntermediateDirectories: true)
        let osVersion = ProcessInfo.processInfo.operatingSystemVersion
        let parameters: TDObject = [
            "@type": "setTdlibParameters",
            "use_test_dc": configuration.useTestDC,
            "database_directory": configuration.databaseDirectory.path,
            "files_directory": configuration.filesDirectory.path,
            "database_encryption_key": configuration.encryptionKey.base64EncodedString(),
            // File DB lets TDLib resume partially uploaded files; message DB stays off so TDLib never
            // re-sends pending messages on its own after relaunch — the UploadQueue owns retries.
            "use_file_database": true,
            "use_chat_info_database": true,
            "use_message_database": false,
            "use_secret_chats": false,
            "api_id": configuration.credentials.apiId,
            "api_hash": configuration.credentials.apiHash,
            "system_language_code": Locale.current.language.languageCode?.identifier ?? "en",
            "device_model": "Mac",
            "system_version": "macOS \(osVersion.majorVersion).\(osVersion.minorVersion)",
            "application_version": configuration.applicationVersion
        ]
        Task { [weak self] in
            do {
                _ = try await self?.request(parameters)
            } catch {
                NSLog("TeleSave: setTdlibParameters failed: \(error)")
            }
        }
    }

    private func handleClosed() {
        lock.lock()
        let old = client
        let restart = restartOnClose
        restartOnClose = false
        let signal = closedSignal
        closedSignal = nil
        client = nil
        lock.unlock()
        old?.failPending(TelegramError(code: 0, message: "Telegram client closed"))
        signal?.signal()
        if restart { makeClient() }
    }

    // MARK: - Auth

    public func requestQRCode() async throws {
        _ = try await request(["@type": "requestQrCodeAuthentication", "other_user_ids": [Int64]()])
    }

    public func submitPhoneNumber(_ phoneNumber: String) async throws {
        _ = try await request([
            "@type": "setAuthenticationPhoneNumber",
            "phone_number": phoneNumber,
            "settings": ["@type": "phoneNumberAuthenticationSettings"] as TDObject
        ])
    }

    public func submitCode(_ code: String) async throws {
        _ = try await request(["@type": "checkAuthenticationCode", "code": code])
    }

    public func submitPassword(_ password: String) async throws {
        _ = try await request(["@type": "checkAuthenticationPassword", "password": password])
    }

    public func logOut() async throws {
        lock.withLock { restartOnClose = true }
        // logOut destroys the local database; TDLib then closes and we start a fresh instance.
        _ = try await request(["@type": "logOut"])
    }

    public func fetchMe() async throws -> TelegramUser {
        let user = try await request(["@type": "getMe"])
        let usernames = user.object("usernames")?["active_usernames"] as? [String]
        return TelegramUser(
            id: user.int64("id") ?? 0,
            firstName: user["first_name"] as? String ?? "",
            lastName: user["last_name"] as? String ?? "",
            username: usernames?.first,
            isPremium: user["is_premium"] as? Bool ?? false,
            photoFileId: user.object("profile_photo")?.object("small")?.int64("id").map { Int32(truncatingIfNeeded: $0) }
        )
    }

    // MARK: - Sending

    private func savedMessagesChat() async throws -> Int64 {
        if let cached = lock.withLock({ savedMessagesChatId }) { return cached }
        let me = try await fetchMe()
        // Saved Messages is the private chat with yourself.
        let chat = try await request(["@type": "createPrivateChat", "user_id": me.id, "force": false])
        guard let chatId = chat.int64("id") else { throw TelegramError(code: 500, message: "Saved Messages chat not found") }
        lock.withLock { savedMessagesChatId = chatId }
        return chatId
    }

    public func send(_ files: [OutgoingFile], to destination: UploadDestination?) async throws -> [SentMessage] {
        guard !files.isEmpty else { return [] }
        let chatId = try await resolveChat(destination)
        let contents = files.map(Self.content(for:))
        var request: TDObject = ["chat_id": chatId]
        if let topic = destination?.forumTopicId {
            request["topic_id"] = ["@type": "messageTopicForum", "forum_topic_id": topic] as TDObject
        }
        let messages: [TDObject]
        if contents.count == 1 {
            request["@type"] = "sendMessage"
            request["input_message_content"] = contents[0]
            messages = [try await self.request(request)]
        } else {
            request["@type"] = "sendMessageAlbum"
            request["input_message_contents"] = contents
            let result = try await self.request(request)
            messages = result["messages"] as? [TDObject] ?? []
        }
        guard messages.count == files.count else {
            throw TelegramError(code: 500, message: "Unexpected TDLib response: \(messages.count) messages for \(files.count) files")
        }
        return zip(messages, files).map { message, file in
            SentMessage(messageId: message.int64("id") ?? 0, fileId: Self.fileId(in: message, path: file.path))
        }
    }

    public func cancel(messageIds: [Int64], in destination: UploadDestination?) async {
        guard let chatId = try? await resolveChat(destination) else { return }
        // Deleting a pending outgoing message cancels its upload.
        _ = try? await request(["@type": "deleteMessages", "chat_id": chatId, "message_ids": messageIds, "revoke": true])
    }

    private func resolveChat(_ destination: UploadDestination?) async throws -> Int64 {
        if let chatId = destination?.chatId { return chatId }
        return try await savedMessagesChat()
    }

    // MARK: - Chats

    public func loadChats(limit: Int) async throws -> [ChatSummary] {
        let main: TDObject = ["@type": "chatListMain"]
        // loadChats pulls the list from the server; it returns 404 once everything is loaded.
        do {
            _ = try await request(["@type": "loadChats", "chat_list": main, "limit": limit])
        } catch let error as TelegramError where error.code == 404 {
        }
        let result = try await request(["@type": "getChats", "chat_list": main, "limit": limit])
        let ids = (result["chat_ids"] as? [NSNumber])?.map(\.int64Value) ?? []
        let myId = try await fetchMe().id
        var chats: [ChatSummary] = []
        for id in ids {
            guard let chat = try? await request(["@type": "getChat", "chat_id": id]),
                  let summary = try? await summarize(chat, myId: myId) else { continue }
            chats.append(summary)
        }
        return chats
    }

    private func summarize(_ chat: TDObject, myId: Int64) async throws -> ChatSummary? {
        guard let id = chat.int64("id"), let type = chat.object("type") else { return nil }
        let title = chat["title"] as? String ?? ""
        switch type["@type"] as? String {
        case "chatTypePrivate":
            let userId = type.int64("user_id") ?? 0
            if userId == myId { return ChatSummary(id: id, title: "Saved Messages", kind: .savedMessages, isForum: false, username: nil) }
            let user = try? await request(["@type": "getUser", "user_id": userId])
            let isBot = (user?.object("type")?["@type"] as? String) == "userTypeBot"
            let username = (user?.object("usernames")?["active_usernames"] as? [String])?.first
            return ChatSummary(id: id, title: title, kind: isBot ? .bot : .user, isForum: false, username: username)
        case "chatTypeBasicGroup":
            return ChatSummary(id: id, title: title, kind: .group, isForum: false, username: nil)
        case "chatTypeSupergroup":
            let isChannel = type["is_channel"] as? Bool ?? false
            let supergroup = try? await request(["@type": "getSupergroup", "supergroup_id": type.int64("supergroup_id") ?? 0])
            let username = (supergroup?.object("usernames")?["active_usernames"] as? [String])?.first
            return ChatSummary(id: id, title: title, kind: isChannel ? .channel : .group,
                               isForum: supergroup?["is_forum"] as? Bool ?? false, username: username)
        default:
            // Secret chats need separate encryption handling; not offered as destinations.
            return nil
        }
    }

    public func canSendFiles(to chatId: Int64) async throws -> Bool {
        let chat = try await request(["@type": "getChat", "chat_id": chatId])
        guard let type = chat.object("type") else { return false }
        let defaults = chat.object("permissions")?["can_send_documents"] as? Bool ?? false
        switch type["@type"] as? String {
        case "chatTypePrivate":
            return true
        case "chatTypeBasicGroup":
            let group = try await request(["@type": "getBasicGroup", "basic_group_id": type.int64("basic_group_id") ?? 0])
            return ChatPermissionRules.canSendFiles(status: group.object("status"), isChannel: false, defaultPermission: defaults)
        case "chatTypeSupergroup":
            let group = try await request(["@type": "getSupergroup", "supergroup_id": type.int64("supergroup_id") ?? 0])
            return ChatPermissionRules.canSendFiles(status: group.object("status"), isChannel: type["is_channel"] as? Bool ?? false, defaultPermission: defaults)
        default:
            return false
        }
    }

    public func forumTopics(in chatId: Int64) async throws -> [ForumTopicSummary] {
        let result = try await request([
            "@type": "getForumTopics", "chat_id": chatId, "query": "",
            "offset_date": 0, "offset_message_id": 0, "offset_forum_topic_id": 0, "limit": 100
        ])
        let topics = result["topics"] as? [TDObject] ?? []
        return topics.compactMap { topic in
            guard let info = topic.object("info"), let id = info.int64("forum_topic_id") else { return nil }
            return ForumTopicSummary(id: Int32(truncatingIfNeeded: id), name: info["name"] as? String ?? "",
                                     isGeneral: info["is_general"] as? Bool ?? false,
                                     isClosed: info["is_closed"] as? Bool ?? false)
        }
    }

    public func downloadFile(_ fileId: Int32) async throws -> String {
        let file = try await request([
            "@type": "downloadFile", "file_id": fileId, "priority": 1,
            "offset": 0, "limit": 0, "synchronous": true
        ])
        guard let path = file.object("local")?["path"] as? String, !path.isEmpty else {
            throw TelegramError(code: 404, message: "File not downloaded")
        }
        return path
    }

    public func downloadMessageFile(messageId: Int64, in destination: UploadDestination?) async throws -> String {
        let chatId = try await resolveChat(destination)
        let message = try await request(["@type": "getMessage", "chat_id": chatId, "message_id": messageId])
        guard let content = message.object("content"),
              let fileId = MessageFileLocator.primaryFileId(content: content) ?? Self.fileId(in: message, path: "") else {
            throw TelegramError(code: 404, message: "Mesajda dosya yok")
        }
        return try await downloadFile(fileId)
    }

    public func deleteMessages(_ messageIds: [Int64], in destination: UploadDestination?) async throws {
        let chatId = try await resolveChat(destination)
        _ = try await request(["@type": "deleteMessages", "chat_id": chatId, "message_ids": messageIds, "revoke": true])
    }

    public func searchChatFiles(in destination: UploadDestination?, query: String, filter: ChatFileFilter,
                                fromMessageId: Int64, limit: Int) async throws -> ChatFilePage {
        let chatId = try await resolveChat(destination)
        var files: [ChatFile] = []
        var from = fromMessageId
        // With the "all" filter text messages come back too and are dropped, so a page can be thin:
        // keep fetching (a few rounds at most) until it is reasonably full.
        for _ in 0..<(filter == .all ? 5 : 1) {
            var request: TDObject = [
                "@type": "searchChatMessages",
                "chat_id": chatId,
                "query": query,
                "from_message_id": from,
                "offset": 0,
                "limit": limit,
                "filter": ["@type": filter.tdFilter] as TDObject
            ]
            if let topic = destination?.forumTopicId {
                request["topic_id"] = ["@type": "messageTopicForum", "forum_topic_id": topic] as TDObject
            }
            let result = try await self.request(request)
            let messages = result["messages"] as? [TDObject] ?? []
            files.append(contentsOf: messages.compactMap(ChatFileParser.parse))
            from = result.int64("next_from_message_id") ?? 0
            if from == 0 || files.count >= limit / 2 { break }
        }
        return ChatFilePage(files: files, nextFromMessageId: from)
    }

    static func content(for file: OutgoingFile) -> TDObject {
        let input: TDObject = ["@type": "inputFileLocal", "path": file.path]
        let caption: TDObject = ["@type": "formattedText", "text": file.caption, "entities": [TDObject]()]
        let dims = file.dimensions
        // TDLib 1.8.5x+ wraps the file in inputDocument / inputPhoto / inputVideo (td_api.tl).
        switch file.kind {
        case .document:
            return [
                "@type": "inputMessageDocument",
                "document": [
                    "@type": "inputDocument",
                    "document": input,
                    "disable_content_type_detection": true
                ] as TDObject,
                "caption": caption
            ]
        case .photo:
            return [
                "@type": "inputMessagePhoto",
                "photo": [
                    "@type": "inputPhoto",
                    "photo": input,
                    "added_sticker_file_ids": [Int](),
                    "width": dims?.width ?? 0,
                    "height": dims?.height ?? 0
                ] as TDObject,
                "caption": caption
            ]
        case .video:
            return [
                "@type": "inputMessageVideo",
                "video": [
                    "@type": "inputVideo",
                    "video": input,
                    "added_sticker_file_ids": [Int](),
                    "duration": dims?.duration ?? 0,
                    "width": dims?.width ?? 0,
                    "height": dims?.height ?? 0,
                    "supports_streaming": true
                ] as TDObject,
                "caption": caption
            ]
        }
    }

    /// Finds the id of the uploading file inside a message, preferring the one with our local path.
    static func fileId(in message: TDObject, path: String) -> Int32? {
        var files: [TDObject] = []
        func walk(_ value: Any) {
            if let dict = value as? TDObject {
                if dict["@type"] as? String == "file" { files.append(dict) }
                dict.values.forEach(walk)
            } else if let array = value as? [Any] {
                array.forEach(walk)
            }
        }
        walk(message["content"] ?? [:])
        let match = files.first { ($0.object("local")?["path"] as? String) == path } ?? files.first
        return match?.int64("id").map { Int32(truncatingIfNeeded: $0) }
    }
}
