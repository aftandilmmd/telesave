import Foundation

/// Copied text sent from the clipboard: as Telegram messages, or as a .txt file.
public enum TextPasteMode: String, CaseIterable, Codable, Sendable {
    case message
    case file
}

/// Splits copied text into Telegram messages. The Windows/Linux app (ClipboardText.cs) follows the same rules.
public enum ClipboardText {
    /// Telegram's limit for one text message, in UTF-16 code units.
    public static let messageLimit = 4096

    /// The text without leading/trailing whitespace, or nil when nothing is left.
    public static func prepare(_ text: String?) -> String? {
        guard let trimmed = text?.trimmingCharacters(in: .whitespacesAndNewlines), !trimmed.isEmpty else { return nil }
        return trimmed
    }

    /// Chunks of at most `limit` UTF-16 units. A chunk ends after the last line break in its second half, else after
    /// the last space there, else at the limit; never inside a character (emoji, combined letters).
    public static func split(_ text: String, limit: Int = messageLimit) -> [String] {
        var chunks: [String] = []
        var rest = Substring(text)
        while true {
            rest = rest.drop(while: \.isWhitespace)
            if rest.isEmpty { break }
            if rest.utf16.count <= limit {
                chunks.append(String(rest))
                break
            }
            var end = rest.startIndex
            var units = 0
            var lastNewline: String.Index?
            var lastSpace: String.Index?
            while end < rest.endIndex {
                let character = rest[end]
                let width = character.utf16.count
                if units + width > limit { break }
                units += width
                end = rest.index(after: end)
                if units >= limit / 2 {
                    if character.isNewline { lastNewline = end } else if character.isWhitespace { lastSpace = end }
                }
            }
            // A single character longer than the limit (pathological): send it whole rather than loop forever.
            if end == rest.startIndex { end = rest.index(after: end) }
            let cut = lastNewline ?? lastSpace ?? end
            var chunk = rest[..<cut]
            while chunk.last?.isWhitespace == true { chunk = chunk.dropLast() }
            if !chunk.isEmpty { chunks.append(String(chunk)) }
            rest = rest[cut...]
        }
        return chunks
    }
}
