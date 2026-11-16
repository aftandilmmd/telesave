import Foundation
import UniformTypeIdentifiers

public struct UploadItem: Identifiable, Codable, Equatable, Sendable {
    public enum State: Codable, Equatable, Sendable {
        case queued
        case uploading(progress: Double)
        case done(messageId: Int64)
        case failed(reason: String)
        case cancelled

        public var isTerminal: Bool {
            switch self {
            case .done, .failed, .cancelled: return true
            case .queued, .uploading: return false
            }
        }
    }

    public let id: UUID
    public var path: String
    public var bookmark: Data?
    public var displayName: String
    public var size: Int64
    /// File was created by TeleSave (zipped folder) and must be deleted when no longer needed.
    public var isTemporary: Bool
    /// Items sharing a group id are sent together as one album.
    public var groupId: UUID?
    /// nil = Saved Messages (also what queue files written before destinations existed decode to).
    public var destination: UploadDestination?
    public var state: State
    public var attempts: Int
    public var notBefore: Date?
    public var messageId: Int64?
    public var createdAt: Date
    public var finishedAt: Date?

    public init(file: PreparedFile, groupId: UUID? = nil, destination: UploadDestination? = nil, now: Date = Date()) {
        self.id = UUID()
        self.path = file.url.path
        self.bookmark = file.bookmark
        self.displayName = file.displayName
        self.size = file.size
        self.isTemporary = file.isTemporary
        self.groupId = groupId
        self.destination = destination
        self.state = .queued
        self.attempts = 0
        self.notBefore = nil
        self.messageId = nil
        self.createdAt = now
        self.finishedAt = nil
    }

    public var progress: Double {
        switch state {
        case .uploading(let p): return p
        case .done: return 1
        default: return 0
        }
    }
}

public struct PreparedFile: Equatable, Sendable {
    public let url: URL
    public let displayName: String
    public let size: Int64
    public let isTemporary: Bool
    public let bookmark: Data?

    public init(url: URL, displayName: String, size: Int64, isTemporary: Bool, bookmark: Data?) {
        self.url = url
        self.displayName = displayName
        self.size = size
        self.isTemporary = isTemporary
        self.bookmark = bookmark
    }
}

public struct RetryPolicy: Equatable, Sendable {
    public var maxAttempts: Int
    public var baseDelay: TimeInterval
    public var maxDelay: TimeInterval
    /// Multiplier applied to server-mandated flood waits (1 in production; tests shrink it).
    public var floodScale: Double

    public init(maxAttempts: Int = 5, baseDelay: TimeInterval = 2, maxDelay: TimeInterval = 60, floodScale: Double = 1) {
        self.maxAttempts = maxAttempts
        self.baseDelay = baseDelay
        self.maxDelay = maxDelay
        self.floodScale = floodScale
    }

    /// Delay before the next attempt, or nil when the item should fail permanently.
    /// `attempts` is the number of attempts already made.
    public func delay(after error: Error, attempts: Int) -> TimeInterval? {
        let kind = (error as? TelegramError)?.kind ?? .transient
        switch kind {
        case .permanent:
            return nil
        case .flood(let seconds):
            // Flood waits do not burn the attempt budget as fast: allow twice as many.
            guard attempts < maxAttempts * 2 else { return nil }
            return Double(seconds) * floodScale
        case .transient:
            guard attempts < maxAttempts else { return nil }
            return min(maxDelay, baseDelay * pow(2, Double(max(0, attempts - 1))))
        }
    }
}

public enum MediaClassifier {
    /// Telegram compresses photos above this size badly or rejects them; send those as documents.
    public static let maxPhotoSize: Int64 = 10 * 1024 * 1024

    public static func kind(forPath path: String, size: Int64, sendMediaCompressed: Bool) -> SendKind {
        guard sendMediaCompressed else { return .document }
        let ext = (path as NSString).pathExtension
        guard let type = UTType(filenameExtension: ext) else { return .document }
        if type.conforms(to: .jpeg) || type.conforms(to: .png) || type.conforms(to: .heic) || type.conforms(to: .webP) {
            return size <= maxPhotoSize ? .photo : .document
        }
        if type.conforms(to: .mpeg4Movie) || type.conforms(to: .quickTimeMovie) || type.conforms(to: .appleProtectedMPEG4Video) {
            return .video
        }
        return .document
    }
}

public enum AlbumGrouper {
    public static let maxAlbumSize = 10

    /// Splits dropped files into album groups. Telegram albums cannot mix documents with photos/videos,
    /// so files are grouped per family and chunked to 10. Single-file chunks get no group id.
    public static func assignGroups(_ files: [PreparedFile], sendMediaCompressed: Bool) -> [(PreparedFile, UUID?)] {
        var media: [PreparedFile] = []
        var documents: [PreparedFile] = []
        for file in files {
            switch MediaClassifier.kind(forPath: file.url.path, size: file.size, sendMediaCompressed: sendMediaCompressed) {
            case .photo, .video: media.append(file)
            case .document: documents.append(file)
            }
        }
        var result: [(PreparedFile, UUID?)] = []
        for family in [media, documents] {
            var index = 0
            while index < family.count {
                let chunk = family[index..<min(index + maxAlbumSize, family.count)]
                let gid: UUID? = chunk.count > 1 ? UUID() : nil
                result.append(contentsOf: chunk.map { ($0, gid) })
                index += maxAlbumSize
            }
        }
        return result
    }
}

public struct CaptionTemplate: Equatable, Sendable {
    public var template: String

    public init(_ template: String) {
        self.template = template
    }

    /// Supported tokens: {filename}, {date}, {size}.
    public func render(fileName: String, size: Int64, date: Date) -> String {
        guard !template.isEmpty else { return "" }
        let dateFormatter = DateFormatter()
        dateFormatter.dateFormat = "yyyy-MM-dd HH:mm"
        return template
            .replacingOccurrences(of: "{filename}", with: fileName)
            .replacingOccurrences(of: "{date}", with: dateFormatter.string(from: date))
            .replacingOccurrences(of: "{size}", with: ByteCountFormatter.string(fromByteCount: size, countStyle: .file))
    }
}
