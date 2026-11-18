import XCTest
@testable import TeleSaveCore

final class ClipboardTextTests: XCTestCase {
    func testPrepareTrimsAndRejectsEmpty() {
        XCTAssertNil(ClipboardText.prepare(nil))
        XCTAssertNil(ClipboardText.prepare(" \n\t "))
        XCTAssertEqual(ClipboardText.prepare("  hi there \n"), "hi there")
    }

    func testShortTextIsOneChunk() {
        XCTAssertEqual(ClipboardText.split("hello"), ["hello"])
        let exact = String(repeating: "a", count: 4096)
        XCTAssertEqual(ClipboardText.split(exact), [exact])
    }

    func testHardSplitWithoutSpaces() {
        let chunks = ClipboardText.split(String(repeating: "a", count: 4097))
        XCTAssertEqual(chunks.map(\.utf16.count), [4096, 1])
    }

    func testPrefersLineBreakInSecondHalf() {
        let first = String(repeating: "a", count: 3000)
        let second = String(repeating: "b", count: 2000)
        XCTAssertEqual(ClipboardText.split(first + "\n" + second), [first, second])
    }

    func testIgnoresLineBreakInFirstHalfAndUsesSpace() {
        let text = "x\n" + String(repeating: "a", count: 3000) + " " + String(repeating: "b", count: 2000)
        let chunks = ClipboardText.split(text)
        XCTAssertEqual(chunks.count, 2)
        XCTAssertEqual(chunks[1], String(repeating: "b", count: 2000))
    }

    func testNeverSplitsAnEmoji() {
        // 4095 units, then a 2-unit emoji that would straddle the limit.
        let text = String(repeating: "a", count: 4095) + "😀" + "z"
        let chunks = ClipboardText.split(text)
        XCTAssertEqual(chunks, [String(repeating: "a", count: 4095), "😀z"])
        XCTAssertTrue(chunks.allSatisfy { $0.utf16.count <= 4096 })
    }

    func testChunksAreTrimmedAndCoverAllText() {
        let words = Array(repeating: "word", count: 3000).joined(separator: " ")
        let chunks = ClipboardText.split(words, limit: 100)
        XCTAssertTrue(chunks.allSatisfy { $0.utf16.count <= 100 && $0.first != " " && $0.last != " " })
        XCTAssertEqual(chunks.joined(separator: " "), words)
    }
}
