import Foundation

/// Permanent record of messages TeleSave sent, so they can be marked in a chat's file list.
/// Telegram messages carry no "sent from app/device" information, so this is local to this Mac.
public struct SentMessageRegistry: Codable, Equatable, Sendable {
    private var keys: Set<String> = []

    public init() {}

    /// `chatId` nil = the account's Saved Messages. Message ids are unique within a chat.
    static func key(owner: Int64, chatId: Int64?, messageId: Int64) -> String {
        "\(owner)|\(chatId.map(String.init) ?? "saved")|\(messageId)"
    }

    public func contains(owner: Int64, chatId: Int64?, messageId: Int64) -> Bool {
        keys.contains(Self.key(owner: owner, chatId: chatId, messageId: messageId))
    }

    /// Returns true when the entry is new.
    @discardableResult
    public mutating func insert(owner: Int64, chatId: Int64?, messageId: Int64) -> Bool {
        keys.insert(Self.key(owner: owner, chatId: chatId, messageId: messageId)).inserted
    }

    @discardableResult
    public mutating func remove(owner: Int64, chatId: Int64?, messageId: Int64) -> Bool {
        keys.remove(Self.key(owner: owner, chatId: chatId, messageId: messageId)) != nil
    }

    public var count: Int { keys.count }

    public static func load(from url: URL) -> SentMessageRegistry {
        guard let data = try? Data(contentsOf: url),
              let registry = try? JSONDecoder().decode(SentMessageRegistry.self, from: data) else { return SentMessageRegistry() }
        return registry
    }

    public func save(to url: URL) {
        do {
            try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
            try JSONEncoder().encode(self).write(to: url, options: .atomic)
        } catch {
            NSLog("TeleSave: sent-message registry save failed: \(error)")
        }
    }
}
