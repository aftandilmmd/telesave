import XCTest
@testable import TeleSaveCore

final class ChatFileParserTests: XCTestCase {
    private let mini = Data([0xFF, 0xD8, 0x01]).base64EncodedString()

    func testDocument() throws {
        let file = try XCTUnwrap(ChatFileParser.parse([
            "id": 1048576, "date": 1_788_000_000, "is_outgoing": true,
            "content": [
                "@type": "messageDocument",
                "caption": ["text": "rapor"],
                "document": [
                    "file_name": "rapor.pdf",
                    "minithumbnail": ["data": mini],
                    "thumbnail": ["file": ["id": 7]],
                    "document": ["id": 9, "size": 2048, "expected_size": 2048]
                ]
            ]
        ]))
        XCTAssertEqual(file.messageId, 1048576)
        XCTAssertEqual(file.fileId, 9)
        XCTAssertEqual(file.kind, .document)
        XCTAssertEqual(file.fileName, "rapor.pdf")
        XCTAssertEqual(file.size, 2048)
        XCTAssertEqual(file.caption, "rapor")
        XCTAssertEqual(file.thumbnailFileId, 7)
        XCTAssertEqual(file.minithumbnail, Data([0xFF, 0xD8, 0x01]))
        XCTAssertTrue(file.isOutgoing)
        XCTAssertEqual(file.date, Date(timeIntervalSince1970: 1_788_000_000))
    }

    func testPhotoUsesLargestForDownloadAndMidSizeForPreview() throws {
        let file = try XCTUnwrap(ChatFileParser.parse([
            "id": 2, "date": 0,
            "content": [
                "@type": "messagePhoto",
                "photo": ["sizes": [
                    ["width": 1280, "height": 960, "photo": ["id": 30, "size": 300_000]],
                    ["width": 90, "height": 67, "photo": ["id": 10, "size": 1_000]],
                    ["width": 320, "height": 240, "photo": ["id": 20, "size": 20_000]]
                ]]
            ]
        ]))
        XCTAssertEqual(file.kind, .photo)
        XCTAssertEqual(file.fileId, 30)
        XCTAssertEqual(file.size, 300_000)
        XCTAssertEqual(file.thumbnailFileId, 20)
        XCTAssertTrue(file.fileName.hasSuffix(".jpg"))
    }

    func testVideoWithoutNameAndExpectedSize() throws {
        let file = try XCTUnwrap(ChatFileParser.parse([
            "id": 3, "date": 0,
            "content": ["@type": "messageVideo", "video": ["file_name": "", "video": ["id": 5, "size": 0, "expected_size": 99]]]
        ]))
        XCTAssertEqual(file.kind, .video)
        XCTAssertEqual(file.size, 99)
        XCTAssertTrue(file.fileName.hasSuffix(".mp4"))
    }

    func testAudioUsesAlbumCover() throws {
        let file = try XCTUnwrap(ChatFileParser.parse([
            "id": 4, "date": 0,
            "content": ["@type": "messageAudio", "audio": [
                "file_name": "song.mp3", "audio": ["id": 6, "size": 10],
                "album_cover_thumbnail": ["file": ["id": 8]]
            ]]
        ]))
        XCTAssertEqual(file.kind, .audio)
        XCTAssertEqual(file.thumbnailFileId, 8)
    }

    func testNonFileMessagesAreSkipped() {
        XCTAssertNil(ChatFileParser.parse(["id": 5, "date": 0, "content": ["@type": "messageText", "text": ["text": "hi"]]]))
        XCTAssertNil(ChatFileParser.parse(["id": 6]))
    }

    func testFilters() {
        XCTAssertEqual(ChatFileFilter.documents.tdFilter, "searchMessagesFilterDocument")
        XCTAssertEqual(ChatFileFilter.media.tdFilter, "searchMessagesFilterPhotoAndVideo")
        XCTAssertTrue(ChatFilePage(files: [], nextFromMessageId: 0).isLastPage)
    }
}

final class SentMessageRegistryTests: XCTestCase {
    func testScopedByAccountAndChat() {
        var registry = SentMessageRegistry()
        XCTAssertTrue(registry.insert(owner: 1, chatId: nil, messageId: 100))
        XCTAssertFalse(registry.insert(owner: 1, chatId: nil, messageId: 100), "already recorded")
        registry.insert(owner: 1, chatId: -500, messageId: 7)

        XCTAssertTrue(registry.contains(owner: 1, chatId: nil, messageId: 100))
        XCTAssertFalse(registry.contains(owner: 2, chatId: nil, messageId: 100), "other account's Saved Messages")
        XCTAssertFalse(registry.contains(owner: 1, chatId: -500, messageId: 100), "same id, other chat")
        XCTAssertTrue(registry.contains(owner: 1, chatId: -500, messageId: 7))

        XCTAssertTrue(registry.remove(owner: 1, chatId: -500, messageId: 7))
        XCTAssertFalse(registry.contains(owner: 1, chatId: -500, messageId: 7))
    }

    func testPersistence() {
        let dir = makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        let url = dir.appendingPathComponent("sent.json")
        var registry = SentMessageRegistry()
        registry.insert(owner: 1, chatId: nil, messageId: 100)
        registry.save(to: url)
        XCTAssertTrue(SentMessageRegistry.load(from: url).contains(owner: 1, chatId: nil, messageId: 100))
        XCTAssertEqual(SentMessageRegistry.load(from: dir.appendingPathComponent("missing.json")).count, 0)
    }
}
