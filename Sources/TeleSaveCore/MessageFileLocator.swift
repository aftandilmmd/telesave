import Foundation

/// Finds the downloadable file inside a TDLib `MessageContent` JSON object.
public enum MessageFileLocator {
    /// The main file: the document itself, the largest photo size, the video (not its thumbnail or cover).
    public static func primaryFileId(content: [String: Any]) -> Int32? {
        func id(_ file: Any?) -> Int32? {
            ((file as? [String: Any])?["id"] as? NSNumber).map { Int32(truncatingIfNeeded: $0.int64Value) }
        }
        func object(_ dict: [String: Any]?, _ key: String) -> [String: Any]? {
            dict?[key] as? [String: Any]
        }
        switch content["@type"] as? String {
        case "messageDocument":
            return id(object(content, "document")?["document"])
        case "messagePhoto":
            let sizes = object(content, "photo")?["sizes"] as? [[String: Any]] ?? []
            let largest = sizes.max { ($0["width"] as? Int ?? 0) * ($0["height"] as? Int ?? 0) < ($1["width"] as? Int ?? 0) * ($1["height"] as? Int ?? 0) }
            return id(largest?["photo"])
        case "messageVideo":
            return id(object(content, "video")?["video"])
        case "messageAudio":
            return id(object(content, "audio")?["audio"])
        case "messageAnimation":
            return id(object(content, "animation")?["animation"])
        case "messageVoiceNote":
            return id(object(content, "voice_note")?["voice"])
        default:
            return nil
        }
    }

    /// A file name in `directory` that does not exist yet: "a.pdf", "a (2).pdf", …
    public static func uniqueURL(in directory: URL, fileName: String) -> URL {
        let fm = FileManager.default
        var candidate = directory.appendingPathComponent(fileName)
        let base = (fileName as NSString).deletingPathExtension
        let ext = (fileName as NSString).pathExtension
        var n = 2
        while fm.fileExists(atPath: candidate.path) {
            let name = ext.isEmpty ? "\(base) (\(n))" : "\(base) (\(n)).\(ext)"
            candidate = directory.appendingPathComponent(name)
            n += 1
        }
        return candidate
    }
}
