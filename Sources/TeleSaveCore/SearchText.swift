import Foundation

/// Accent- and case-insensitive matching tuned for Turkish and Azerbaijani: "Iğdır", "IGDIR" and "igdir" match;
/// so do "Əli" and "eli", "ŞEMA" and "sema".
public enum SearchText {
    public static func normalize(_ text: String) -> String {
        // Letters that have no Unicode decomposition (so diacritic folding alone would keep them).
        let mapped = text
            .replacingOccurrences(of: "ı", with: "i")
            .replacingOccurrences(of: "I", with: "i")
            .replacingOccurrences(of: "İ", with: "i")
            .replacingOccurrences(of: "ə", with: "e")
            .replacingOccurrences(of: "Ə", with: "e")
        return mapped.folding(options: [.caseInsensitive, .diacriticInsensitive, .widthInsensitive], locale: Locale(identifier: "en_US_POSIX"))
    }

    /// Every word of `query` appears somewhere in `text`, in any order.
    public static func matches(_ text: String, query: String) -> Bool {
        let haystack = normalize(text)
        let words = normalize(query).split(whereSeparator: { $0.isWhitespace })
        return words.allSatisfy { haystack.contains($0) }
    }
}
