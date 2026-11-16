import Foundation

/// A file message in a chat, as listed in the "Dosyalar" tab.
public struct ChatFile: Identifiable, Hashable, Sendable {
    public enum Kind: String, Sendable {
        case document
        case photo
        case video
        case audio
        case animation
        case voice
    }

    public let messageId: Int64
    public let fileId: Int32
    public let kind: Kind
    public let fileName: String
    public let size: Int64
    public let date: Date
    public let caption: String
    /// Tiny inline JPEG (~40 px) shown instantly while the real thumbnail downloads.
    public let minithumbnail: Data?
    public let thumbnailFileId: Int32?
    public let isOutgoing: Bool

    public var id: Int64 { messageId }

    public init(messageId: Int64, fileId: Int32, kind: Kind, fileName: String, size: Int64, date: Date, caption: String,
                minithumbnail: Data?, thumbnailFileId: Int32?, isOutgoing: Bool) {
        self.messageId = messageId
        self.fileId = fileId
        self.kind = kind
        self.fileName = fileName
        self.size = size
        self.date = date
        self.caption = caption
        self.minithumbnail = minithumbnail
        self.thumbnailFileId = thumbnailFileId
        self.isOutgoing = isOutgoing
    }
}

public enum ChatFileFilter: String, CaseIterable, Sendable {
    case all
    case documents
    case media
    case audio

    /// TDLib `SearchMessagesFilter`. `all` searches every message and keeps only file messages.
    public var tdFilter: String {
        switch self {
        case .all: return "searchMessagesFilterEmpty"
        case .documents: return "searchMessagesFilterDocument"
        case .media: return "searchMessagesFilterPhotoAndVideo"
        case .audio: return "searchMessagesFilterAudio"
        }
    }
}

public struct ChatFilePage: Equatable, Sendable {
    public var files: [ChatFile]
    /// Pass as `fromMessageId` for the next page; 0 means there are no more.
    public var nextFromMessageId: Int64

    public init(files: [ChatFile], nextFromMessageId: Int64) {
        self.files = files
        self.nextFromMessageId = nextFromMessageId
    }

    public var isLastPage: Bool { nextFromMessageId == 0 }
}

/// Turns a TDLib `message` JSON object into a ChatFile; nil for messages without a file.
public enum ChatFileParser {
    public static func parse(_ message: [String: Any]) -> ChatFile? {
        guard let messageId = int64(message["id"]),
              let content = message["content"] as? [String: Any] else { return nil }
        let date = Date(timeIntervalSince1970: TimeInterval(int64(message["date"]) ?? 0))
        let caption = ((content["caption"] as? [String: Any])?["text"] as? String) ?? ""
        let isOutgoing = message["is_outgoing"] as? Bool ?? false

        let kind: ChatFile.Kind
        let media: [String: Any]?
        let file: [String: Any]?
        var name: String
        var thumbnail: [String: Any]?
        var mini: [String: Any]?

        switch content["@type"] as? String {
        case "messageDocument":
            kind = .document
            media = content["document"] as? [String: Any]
            file = media?["document"] as? [String: Any]
            name = media?["file_name"] as? String ?? ""
        case "messagePhoto":
            kind = .photo
            media = content["photo"] as? [String: Any]
            let sizes = (media?["sizes"] as? [[String: Any]] ?? []).sorted { area($0) < area($1) }
            file = sizes.last?["photo"] as? [String: Any]
            // Smallest size that is still sharp at list-row size.
            let preview = sizes.first { (int64($0["width"]) ?? 0) >= 160 } ?? sizes.last
            thumbnail = preview.map { ["file": $0["photo"] as Any] }
            name = L("Fotoğraf %@.jpg", fileNameDate(date))
        case "messageVideo":
            kind = .video
            media = content["video"] as? [String: Any]
            file = media?["video"] as? [String: Any]
            name = media?["file_name"] as? String ?? ""
            if name.isEmpty { name = L("Video %@.mp4", fileNameDate(date)) }
        case "messageAudio":
            kind = .audio
            media = content["audio"] as? [String: Any]
            file = media?["audio"] as? [String: Any]
            name = media?["file_name"] as? String ?? ""
            thumbnail = media?["album_cover_thumbnail"] as? [String: Any]
            mini = media?["album_cover_minithumbnail"] as? [String: Any]
        case "messageAnimation":
            kind = .animation
            media = content["animation"] as? [String: Any]
            file = media?["animation"] as? [String: Any]
            name = media?["file_name"] as? String ?? ""
            if name.isEmpty { name = L("GIF %@.mp4", fileNameDate(date)) }
        case "messageVoiceNote":
            kind = .voice
            media = content["voice_note"] as? [String: Any]
            file = media?["voice"] as? [String: Any]
            name = L("Sesli mesaj %@.ogg", fileNameDate(date))
        default:
            return nil
        }

        guard let file, let fileId = int64(file["id"]) else { return nil }
        if thumbnail == nil { thumbnail = media?["thumbnail"] as? [String: Any] }
        if mini == nil { mini = media?["minithumbnail"] as? [String: Any] }
        if name.isEmpty { name = L("Dosya %@", fileNameDate(date)) }
        let size = int64(file["size"]).flatMap { $0 > 0 ? $0 : nil } ?? int64(file["expected_size"]) ?? 0
        let thumbnailFileId = ((thumbnail?["file"] as? [String: Any]).flatMap { int64($0["id"]) }).map { Int32(truncatingIfNeeded: $0) }
        // TDLib JSON encodes `bytes` as base64.
        let miniData = (mini?["data"] as? String).flatMap { Data(base64Encoded: $0) }

        return ChatFile(messageId: messageId, fileId: Int32(truncatingIfNeeded: fileId), kind: kind, fileName: name,
                        size: size, date: date, caption: caption, minithumbnail: miniData,
                        thumbnailFileId: thumbnailFileId, isOutgoing: isOutgoing)
    }

    private static func int64(_ value: Any?) -> Int64? {
        if let n = value as? NSNumber { return n.int64Value }
        if let s = value as? String { return Int64(s) }
        return nil
    }

    private static func area(_ size: [String: Any]) -> Int64 {
        (int64(size["width"]) ?? 0) * (int64(size["height"]) ?? 0)
    }

    private static func fileNameDate(_ date: Date) -> String {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "yyyy-MM-dd HH.mm.ss"
        return formatter.string(from: date)
    }
}
