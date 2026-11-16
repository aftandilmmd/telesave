import XCTest
@testable import TeleSaveCore

final class L10nTests: XCTestCase {
    override func tearDown() {
        L10n.selectedCode = L10n.systemCode
    }

    func testLanguageFilesLoadInOrder() {
        XCTAssertEqual(L10n.languages.map(\.code), ["az", "tr", "en", "ru"])
        XCTAssertEqual(L10n.languages.first?.name, "Azərbaycan dili")
    }

    func testEveryLanguageHasEveryKeyWithMatchingPlaceholders() {
        let all = L10n.allStrings
        let reference = all["tr"] ?? [:]
        XCTAssertFalse(reference.isEmpty)
        for (code, strings) in all {
            XCTAssertEqual(Set(strings.keys), Set(reference.keys), "\(code).json keys differ from tr.json")
            for (key, value) in strings {
                XCTAssertEqual(L10n.placeholderCount(value), L10n.placeholderCount(key), "\(code): \(key)")
            }
        }
    }

    /// Every L("…") literal in the sources must exist in the language files.
    func testAllUsedKeysAreTranslated() throws {
        let sources = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
            .appendingPathComponent("Sources")
        let enumerator = try XCTUnwrap(FileManager.default.enumerator(at: sources, includingPropertiesForKeys: nil))
        let pattern = try NSRegularExpression(pattern: #"\bL\("((?:[^"\\]|\\.)*)""#)
        let keys = Set(L10n.allStrings["en"]?.keys ?? [:].keys)
        var missing: [String] = []
        for case let file as URL in enumerator where file.pathExtension == "swift" {
            let text = try String(contentsOf: file, encoding: .utf8)
            for match in pattern.matches(in: text, range: NSRange(text.startIndex..., in: text)) {
                let key = String(text[Range(match.range(at: 1), in: text)!])
                if !keys.contains(key) { missing.append("\(file.lastPathComponent): \(key)") }
            }
        }
        XCTAssertEqual(missing, [])
    }

    func testSelectionAndFallback() {
        L10n.selectedCode = "ru"
        XCTAssertEqual(L("Ayarlar"), "Настройки")
        L10n.selectedCode = "az"
        XCTAssertEqual(L("Ayarlar"), "Parametrlər")
        L10n.selectedCode = "tr"
        XCTAssertEqual(L("Saved Messages"), "Kayıtlı Mesajlar")
        XCTAssertEqual(L("bilinmeyen anahtar"), "bilinmeyen anahtar", "unknown key falls back to itself")
    }

    func testSystemResolutionFallsBackToEnglish() {
        let available: Set<String> = ["az", "tr", "en", "ru"]
        XCTAssertEqual(L10n.resolve("system", available: available, preferredLanguages: ["de-DE", "ru-RU"]), "ru")
        XCTAssertEqual(L10n.resolve("system", available: available, preferredLanguages: ["az-Latn-AZ"]), "az")
        XCTAssertEqual(L10n.resolve("system", available: available, preferredLanguages: ["de-DE", "fr-FR"]), "en")
        XCTAssertEqual(L10n.resolve("tr", available: available, preferredLanguages: ["en-US"]), "tr")
        XCTAssertEqual(L10n.resolve("xx", available: available, preferredLanguages: []), "en")
    }

    func testPlaceholders() {
        L10n.selectedCode = "en"
        XCTAssertEqual(L("%@ indirilemedi: %@", "a.pdf", "timeout"), "Couldn't download a.pdf: timeout")
        XCTAssertEqual(L10n.format("%2$@ ← %1$@", ["a", "b"]), "b ← a")
    }
}
