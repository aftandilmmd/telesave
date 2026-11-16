import Foundation
import UniformTypeIdentifiers

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
    public let mimeType: String

    public var id: Int64 { messageId }

    public init(messageId: Int64, fileId: Int32, kind: Kind, fileName: String, size: Int64, date: Date, caption: String,
                minithumbnail: Data?, thumbnailFileId: Int32?, isOutgoing: Bool, mimeType: String = "") {
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
        self.mimeType = mimeType
    }

    private var contentType: UTType? {
        UTType(mimeType: mimeType) ?? UTType(filenameExtension: (fileName as NSString).pathExtension)
    }

    /// A photo, or an image sent as a document (TeleSave's default for original quality).
    public var isImage: Bool {
        kind == .photo || (kind == .document && contentType?.conforms(to: .image) == true)
    }

    /// A video, or a video sent as a document.
    public var isVideo: Bool {
        kind == .video || (kind == .document && contentType?.conforms(to: .movie) == true)
    }
}

public enum ChatFileFilter: String, CaseIterable, Sendable {
    case all
    case documents
    case images
    case videos
    case audio

    /// One TDLib search per stream; results are filtered by `keep` and merged newest-first.
    /// Images and videos also search documents, because files sent in original quality are documents.
    public var streams: [ChatFileStream] {
        switch self {
        case .all: return [ChatFileStream(tdFilter: "searchMessagesFilterEmpty") { _ in true }]
        case .documents: return [ChatFileStream(tdFilter: "searchMessagesFilterDocument") { !$0.isImage && !$0.isVideo }]
        case .images: return [
            ChatFileStream(tdFilter: "searchMessagesFilterPhoto") { _ in true },
            ChatFileStream(tdFilter: "searchMessagesFilterDocument") { $0.isImage }
        ]
        case .videos: return [
            ChatFileStream(tdFilter: "searchMessagesFilterVideo") { _ in true },
            ChatFileStream(tdFilter: "searchMessagesFilterDocument") { $0.isVideo }
        ]
        case .audio: return [ChatFileStream(tdFilter: "searchMessagesFilterAudio") { _ in true }]
        }
    }
}

public struct ChatFileStream: Sendable {
    public let tdFilter: String
    public let keep: @Sendable (ChatFile) -> Bool
}

/// Where the next page continues: per-stream search position plus items fetched but not yet shown.
public struct ChatFileCursor: Equatable, Sendable {
    struct StreamState: Equatable, Sendable {
        var fromMessageId: Int64 = 0
        var buffer: [ChatFile] = []
        var exhausted = false
    }
    var streams: [StreamState]
}

public struct ChatFilePage: Equatable, Sendable {
    public var files: [ChatFile]
    /// nil when everything has been listed.
    public var cursor: ChatFileCursor?

    public init(files: [ChatFile], cursor: ChatFileCursor?) {
        self.files = files
        self.cursor = cursor
    }

    public var isLastPage: Bool { cursor == nil }
}

/// Merges one or more TDLib searches into a single newest-first list, page by page.
public enum ChatFileSearch {
    /// Fetches one page of one stream: (messages already parsed to files, next from_message_id; 0 = end).
    public typealias Fetch = @Sendable (_ tdFilter: String, _ fromMessageId: Int64, _ limit: Int) async throws -> ([ChatFile], Int64)

    /// Upper bound on server requests per page, so sparse streams cannot loop for long.
    static let maxRequestsPerPage = 8

    /// `query` is matched locally against file name and caption (see `SearchText`): Telegram's server
    /// search does not reliably match file names and is not accent-insensitive.
    public static func page(filter: ChatFileFilter, query: String = "", cursor: ChatFileCursor?, limit: Int, fetch: Fetch) async throws -> ChatFilePage {
        let trimmed = query.trimmingCharacters(in: .whitespacesAndNewlines)
        let specs = filter.streams.map { stream in
            trimmed.isEmpty ? stream : ChatFileStream(tdFilter: stream.tdFilter) {
                stream.keep($0) && SearchText.matches($0.fileName + " " + $0.caption, query: trimmed)
            }
        }
        var state = cursor ?? ChatFileCursor(streams: specs.map { _ in .init() })
        var files: [ChatFile] = []
        var requests = 0

        while files.count < limit {
            // Refill: a stream that may still have items must have a buffered head before we can merge,
            // otherwise an older item from another stream could be shown before a newer one from it.
            for i in state.streams.indices where state.streams[i].buffer.isEmpty && !state.streams[i].exhausted {
                guard requests < maxRequestsPerPage else { break }
                requests += 1
                let (fetched, next) = try await fetch(specs[i].tdFilter, state.streams[i].fromMessageId, limit)
                state.streams[i].buffer.append(contentsOf: fetched.filter(specs[i].keep))
                state.streams[i].fromMessageId = next
                state.streams[i].exhausted = next == 0
            }
            let waiting = state.streams.contains { $0.buffer.isEmpty && !$0.exhausted }
            if waiting && requests >= maxRequestsPerPage { break }
            if waiting { continue }

            // Merge: newest head first, until a live stream runs dry.
            var emitted = false
            while files.count < limit {
                guard let best = state.streams.indices
                    .filter({ !state.streams[$0].buffer.isEmpty })
                    .max(by: { state.streams[$0].buffer[0].messageId < state.streams[$1].buffer[0].messageId }) else { break }
                files.append(state.streams[best].buffer.removeFirst())
                emitted = true
                if state.streams[best].buffer.isEmpty && !state.streams[best].exhausted { break }
            }
            let done = state.streams.allSatisfy { $0.exhausted && $0.buffer.isEmpty }
            if done { return ChatFilePage(files: files, cursor: nil) }
            if !emitted && requests >= maxRequestsPerPage { break }
        }
        let done = state.streams.allSatisfy { $0.exhausted && $0.buffer.isEmpty }
        return ChatFilePage(files: files, cursor: done ? nil : state)
    }
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
        let mimeType = media?["mime_type"] as? String ?? (kind == .photo ? "image/jpeg" : "")
        if thumbnail == nil { thumbnail = media?["thumbnail"] as? [String: Any] }
        if mini == nil { mini = media?["minithumbnail"] as? [String: Any] }
        if name.isEmpty { name = L("Dosya %@", fileNameDate(date)) }
        let size = int64(file["size"]).flatMap { $0 > 0 ? $0 : nil } ?? int64(file["expected_size"]) ?? 0
        let thumbnailFileId = ((thumbnail?["file"] as? [String: Any]).flatMap { int64($0["id"]) }).map { Int32(truncatingIfNeeded: $0) }
        // TDLib JSON encodes `bytes` as base64.
        let miniData = (mini?["data"] as? String).flatMap { Data(base64Encoded: $0) }

        return ChatFile(messageId: messageId, fileId: Int32(truncatingIfNeeded: fileId), kind: kind, fileName: name,
                        size: size, date: date, caption: caption, minithumbnail: miniData,
                        thumbnailFileId: thumbnailFileId, isOutgoing: isOutgoing, mimeType: mimeType)
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
