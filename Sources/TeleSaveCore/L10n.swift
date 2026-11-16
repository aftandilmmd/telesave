import Foundation

/// A UI language, read from `Resources/Localization/<code>.json`.
public struct LanguageInfo: Identifiable, Hashable, Sendable {
    /// ISO 639-1 code, also the file name ("az", "tr", "en", "ru", …).
    public let code: String
    /// Name in the language itself, shown in the language picker.
    public let name: String
    public let locale: Locale
    let order: Int

    public var id: String { code }
}

/// Translations loaded from one JSON file per language:
///
///     { "_language": { "name": "Русский", "locale": "ru_RU", "order": 4 },
///       "strings": { "<Turkish key>": "<translation>", … } }
///
/// Keys are the Turkish UI text. Placeholders are `%@` (all arguments are strings); a translation
/// may reorder them with `%1$@`, `%2$@`. To add a language, drop a new file next to the others.
public enum L10n {
    /// `selectedCode` value meaning "follow macOS".
    public static let systemCode = "system"
    /// Used when the system language is not available and for strings missing from a language file.
    public static let fallbackCode = "en"

    private struct Catalog {
        var languages: [LanguageInfo]
        var strings: [String: [String: String]]
    }

    private struct FileFormat: Decodable {
        struct Meta: Decodable {
            let name: String
            let locale: String
            let order: Int?
        }
        let _language: Meta
        let strings: [String: String]
    }

    private static let catalog: Catalog = loadCatalog(from: Bundle.module.resourceURL?.appendingPathComponent("Localization"))

    private static let lock = NSLock()
    nonisolated(unsafe) private static var _selectedCode = systemCode

    /// Language picked in Settings: a language code or `systemCode`.
    public static var selectedCode: String {
        get { lock.withLock { _selectedCode } }
        set { lock.withLock { _selectedCode = newValue } }
    }

    /// Available languages in picker order.
    public static var languages: [LanguageInfo] { catalog.languages }

    /// Code of the language strings are shown in right now.
    public static var currentCode: String {
        resolve(selectedCode, available: Set(catalog.strings.keys), preferredLanguages: Locale.preferredLanguages)
    }

    /// Language "System" resolves to right now (shown next to the option in the picker).
    public static var systemLanguageCode: String {
        resolve(systemCode, available: Set(catalog.strings.keys), preferredLanguages: Locale.preferredLanguages)
    }

    static func resolve(_ selected: String, available: Set<String>, preferredLanguages: [String]) -> String {
        if selected != systemCode, available.contains(selected) { return selected }
        for identifier in preferredLanguages {
            let code = Locale(identifier: identifier).language.languageCode?.identifier ?? String(identifier.prefix(2))
            if available.contains(code) { return code }
        }
        return fallbackCode
    }

    /// Locale for dates, numbers and durations in the current language.
    public static var locale: Locale {
        let code = currentCode
        return catalog.languages.first { $0.code == code }?.locale ?? Locale(identifier: "en_US")
    }

    public static func string(_ key: String, language: String? = nil, _ args: [String]) -> String {
        let code = language ?? currentCode
        let template = catalog.strings[code]?[key] ?? catalog.strings[fallbackCode]?[key] ?? key
        return format(template, args)
    }

    static func format(_ template: String, _ args: [String]) -> String {
        guard !args.isEmpty else { return template }
        var result = template
        // Positional placeholders first, then sequential ones.
        for (index, arg) in args.enumerated() {
            result = result.replacingOccurrences(of: "%\(index + 1)$@", with: arg)
        }
        var next = 0
        while next < args.count, let range = result.range(of: "%@") {
            result.replaceSubrange(range, with: args[next])
            next += 1
        }
        return result
    }

    /// Number of distinct arguments a template uses.
    static func placeholderCount(_ template: String) -> Int {
        let positional = (1...9).filter { template.contains("%\($0)$@") }.count
        return positional > 0 ? positional : template.components(separatedBy: "%@").count - 1
    }

    /// All keys per language code (for tests).
    static var allStrings: [String: [String: String]] { catalog.strings }

    private static func loadCatalog(from directory: URL?) -> Catalog {
        guard let directory,
              let files = try? FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil) else {
            return Catalog(languages: [], strings: [:])
        }
        var languages: [LanguageInfo] = []
        var strings: [String: [String: String]] = [:]
        for file in files where file.pathExtension == "json" {
            guard let data = try? Data(contentsOf: file),
                  let parsed = try? JSONDecoder().decode(FileFormat.self, from: data) else {
                NSLog("TeleSave: unreadable language file \(file.lastPathComponent)")
                continue
            }
            let code = file.deletingPathExtension().lastPathComponent
            strings[code] = parsed.strings
            languages.append(LanguageInfo(code: code, name: parsed._language.name,
                                          locale: Locale(identifier: parsed._language.locale),
                                          order: parsed._language.order ?? Int.max))
        }
        languages.sort { ($0.order, $0.name) < ($1.order, $1.name) }
        return Catalog(languages: languages, strings: strings)
    }
}

/// Localized string for the Turkish key `key`, with `%@` placeholders filled from `args`.
public func L(_ key: String, _ args: String...) -> String {
    L10n.string(key, args)
}

extension UploadDestination {
    /// Title for display: Saved Messages in the current language, otherwise the chat title.
    public var displayTitle: String {
        isSavedMessages ? L("Saved Messages") : title
    }
}
