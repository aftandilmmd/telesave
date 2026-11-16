import Foundation

public struct ApiCredentials: Equatable, Sendable, Codable {
    public let apiId: Int
    public let apiHash: String

    public init(apiId: Int, apiHash: String) {
        self.apiId = apiId
        self.apiHash = apiHash
    }

    public var isValid: Bool {
        apiId > 0 && apiHash.count == 32 && apiHash.allSatisfy(\.isHexDigit)
    }
}

public struct TelegramUser: Equatable, Sendable {
    public let id: Int64
    public let firstName: String
    public let lastName: String
    public let username: String?
    public let isPremium: Bool
    /// TDLib file id of the small (160×160) profile photo, if the user has one.
    public let photoFileId: Int32?

    public init(id: Int64, firstName: String, lastName: String, username: String?, isPremium: Bool, photoFileId: Int32? = nil) {
        self.id = id
        self.firstName = firstName
        self.lastName = lastName
        self.username = username
        self.isPremium = isPremium
        self.photoFileId = photoFileId
    }

    public var displayName: String {
        [firstName, lastName].filter { !$0.isEmpty }.joined(separator: " ")
    }

    /// Telegram per-file upload limit: 2000 MiB, or 4000 MiB with Premium.
    public var maxUploadSize: Int64 {
        (isPremium ? 4000 : 2000) * 1024 * 1024
    }
}

public enum SendKind: String, Codable, Sendable {
    case document
    case photo
    case video
}

public struct MediaDimensions: Equatable, Sendable {
    public var width: Int
    public var height: Int
    public var duration: Int

    public init(width: Int, height: Int, duration: Int = 0) {
        self.width = width
        self.height = height
        self.duration = duration
    }
}

public struct OutgoingFile: Equatable, Sendable {
    public let path: String
    public let kind: SendKind
    public let caption: String
    public let dimensions: MediaDimensions?

    public init(path: String, kind: SendKind, caption: String, dimensions: MediaDimensions? = nil) {
        self.path = path
        self.kind = kind
        self.caption = caption
        self.dimensions = dimensions
    }
}

/// A message TDLib accepted for sending. `messageId` is temporary until `sendSucceeded`.
public struct SentMessage: Equatable, Sendable {
    public let messageId: Int64
    public let fileId: Int32?

    public init(messageId: Int64, fileId: Int32?) {
        self.messageId = messageId
        self.fileId = fileId
    }
}

/// Where an upload goes. `chatId == nil` means the user's own Saved Messages.
public struct UploadDestination: Codable, Hashable, Sendable {
    public var chatId: Int64?
    /// Forum topic inside a forum supergroup.
    public var forumTopicId: Int32?
    public var title: String
    public var username: String?

    public init(chatId: Int64?, forumTopicId: Int32? = nil, title: String, username: String? = nil) {
        self.chatId = chatId
        self.forumTopicId = forumTopicId
        self.title = title
        self.username = username
    }

    public static let savedMessages = UploadDestination(chatId: nil, title: "Saved Messages")

    public var isSavedMessages: Bool { chatId == nil }
}

public struct ChatSummary: Identifiable, Hashable, Sendable {
    public enum Kind: String, Sendable {
        case savedMessages
        case user
        case bot
        case group
        case channel
    }

    public let id: Int64
    public let title: String
    public let kind: Kind
    public let isForum: Bool
    public let username: String?

    public init(id: Int64, title: String, kind: Kind, isForum: Bool, username: String?) {
        self.id = id
        self.title = title
        self.kind = kind
        self.isForum = isForum
        self.username = username
    }
}

public struct ForumTopicSummary: Identifiable, Hashable, Sendable {
    public let id: Int32
    public let name: String
    public let isGeneral: Bool
    public let isClosed: Bool

    public init(id: Int32, name: String, isGeneral: Bool, isClosed: Bool) {
        self.id = id
        self.name = name
        self.isGeneral = isGeneral
        self.isClosed = isClosed
    }
}

public enum ConnectionState: Equatable, Sendable {
    case waitingForNetwork
    case connectingToProxy
    case connecting
    case updating
    case ready
}

public struct TelegramError: Error, Equatable, Sendable, LocalizedError {
    public let code: Int
    public let message: String

    public init(code: Int, message: String) {
        self.code = code
        self.message = message
    }

    public var errorDescription: String? { message }

    public enum FailureKind: Equatable, Sendable {
        case flood(seconds: Int)
        case transient
        case permanent
    }

    public var kind: FailureKind {
        if code == 429 || message.hasPrefix("FLOOD_WAIT") || message.contains("retry after") {
            return .flood(seconds: Self.floodSeconds(in: message) ?? 30)
        }
        if code == 0 || code >= 500 {
            return .transient
        }
        let upper = message.uppercased()
        if upper.contains("TIMEOUT") || upper.contains("NETWORK") || upper.contains("CONNECTION") {
            return .transient
        }
        return .permanent
    }

    static func floodSeconds(in message: String) -> Int? {
        // "Too Many Requests: retry after 17" or "FLOOD_WAIT_17"
        let digits = message.reversed().prefix { $0.isNumber }
        return digits.isEmpty ? nil : Int(String(digits.reversed()))
    }
}

public enum TelegramEvent: Sendable {
    case auth(AuthState)
    case connection(ConnectionState)
    case fileProgress(fileId: Int32, uploaded: Int64, total: Int64)
    case sendSucceeded(oldMessageId: Int64, newMessageId: Int64)
    case sendFailed(oldMessageId: Int64, error: TelegramError)
}

/// The part of the Telegram client the upload queue needs.
public protocol UploadTransport: AnyObject, Sendable {
    /// Sends one file, or an album when `files.count > 1`, to `destination` (nil = Saved Messages).
    /// Returns one SentMessage per file, in order.
    func send(_ files: [OutgoingFile], to destination: UploadDestination?) async throws -> [SentMessage]
    /// Cancels pending sends (deletes the not-yet-sent messages).
    func cancel(messageIds: [Int64], in destination: UploadDestination?) async
}

public protocol TelegramClient: UploadTransport {
    func start()
    func requestQRCode() async throws
    func submitPhoneNumber(_ phoneNumber: String) async throws
    func submitCode(_ code: String) async throws
    func submitPassword(_ password: String) async throws
    func logOut() async throws
    func fetchMe() async throws -> TelegramUser
    /// Most recent chats from the main list (secret chats excluded).
    func loadChats(limit: Int) async throws -> [ChatSummary]
    /// Whether the current user may send files to the chat (admin rights in channels, document permission in groups).
    func canSendFiles(to chatId: Int64) async throws -> Bool
    func forumTopics(in chatId: Int64) async throws -> [ForumTopicSummary]
    /// Downloads a Telegram file (e.g. a profile photo) and returns its local path.
    func downloadFile(_ fileId: Int32) async throws -> String
    /// Downloads the file of a sent message and returns its local path.
    func downloadMessageFile(messageId: Int64, in destination: UploadDestination?) async throws -> String
    /// Deletes messages for everyone.
    func deleteMessages(_ messageIds: [Int64], in destination: UploadDestination?) async throws
    /// One page of file messages in a chat, newest first. `fromMessageId` 0 starts at the newest.
    func searchChatFiles(in destination: UploadDestination?, query: String, filter: ChatFileFilter,
                         fromMessageId: Int64, limit: Int) async throws -> ChatFilePage
    func close()
}
