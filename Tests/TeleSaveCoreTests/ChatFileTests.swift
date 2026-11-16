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

    func testFilterStreams() {
        XCTAssertEqual(ChatFileFilter.images.streams.map(\.tdFilter), ["searchMessagesFilterPhoto", "searchMessagesFilterDocument"])
        XCTAssertEqual(ChatFileFilter.videos.streams.map(\.tdFilter), ["searchMessagesFilterVideo", "searchMessagesFilterDocument"])
        XCTAssertEqual(ChatFileFilter.documents.streams.map(\.tdFilter), ["searchMessagesFilterDocument"])
        XCTAssertTrue(ChatFilePage(files: [], cursor: nil).isLastPage)
    }

    func testImageAndVideoDetectionIncludesDocuments() {
        func doc(_ name: String, mime: String = "") -> ChatFile {
            ChatFile(messageId: 1, fileId: 1, kind: .document, fileName: name, size: 1, date: Date(), caption: "",
                     minithumbnail: nil, thumbnailFileId: nil, isOutgoing: true, mimeType: mime)
        }
        XCTAssertTrue(doc("IMG_1.HEIC").isImage)
        XCTAssertTrue(doc("x", mime: "image/png").isImage)
        XCTAssertTrue(doc("clip.mov").isVideo)
        XCTAssertFalse(doc("rapor.pdf").isImage)
        XCTAssertFalse(doc("rapor.pdf").isVideo)
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

final class ChatFileSearchTests: XCTestCase {
    private func file(_ id: Int64, _ name: String, kind: ChatFile.Kind = .document) -> ChatFile {
        ChatFile(messageId: id, fileId: Int32(id), kind: kind, fileName: name, size: 1, date: Date(), caption: "",
                 minithumbnail: nil, thumbnailFileId: nil, isOutgoing: true)
    }

    /// Fake server: each filter has its messages newest-first; pages are `limit` long.
    private final class Server: @unchecked Sendable {
        let data: [String: [ChatFile]]
        private(set) var requests = 0
        init(_ data: [String: [ChatFile]]) { self.data = data }

        func fetch(_ filter: String, _ from: Int64, _ limit: Int) -> ([ChatFile], Int64) {
            requests += 1
            let all = data[filter] ?? []
            let start = from == 0 ? 0 : (all.firstIndex { $0.messageId < from } ?? all.count)
            let page = Array(all[start..<min(start + limit, all.count)])
            let next = start + limit < all.count ? page.last!.messageId : 0
            return (page, next)
        }
    }

    private func collectAll(_ filter: ChatFileFilter, server: Server, limit: Int) async throws -> [Int64] {
        var ids: [Int64] = []
        var cursor: ChatFileCursor?
        repeat {
            let page = try await ChatFileSearch.page(filter: filter, cursor: cursor, limit: limit) { f, from, l in server.fetch(f, from, l) }
            ids += page.files.map(\.messageId)
            cursor = page.cursor
        } while cursor != nil
        return ids
    }

    func testImagesMergePhotosAndImageDocumentsNewestFirstAcrossPages() async throws {
        let server = Server([
            "searchMessagesFilterPhoto": [file(90, "p", kind: .photo), file(60, "p", kind: .photo), file(20, "p", kind: .photo)],
            "searchMessagesFilterDocument": [
                file(100, "a.jpg"), file(80, "report.pdf"), file(70, "b.png"), file(50, "c.heic"),
                file(40, "notes.txt"), file(10, "d.jpeg")
            ]
        ])
        let ids = try await collectAll(.images, server: server, limit: 2)
        XCTAssertEqual(ids, [100, 90, 70, 60, 50, 20, 10], "merged by recency, PDFs/TXT excluded, no gaps or duplicates")
    }

    func testDocumentsExcludeImagesAndVideos() async throws {
        let server = Server(["searchMessagesFilterDocument": [file(3, "a.pdf"), file(2, "b.mp4"), file(1, "c.png")]])
        let ids = try await collectAll(.documents, server: server, limit: 10)
        XCTAssertEqual(ids, [3])
    }

    func testSparseStreamDoesNotLoopForever() async throws {
        // 200 PDFs and no images: every document page is filtered out entirely.
        let docs = (1...200).reversed().map { file(Int64($0), "f\($0).pdf") }
        let server = Server(["searchMessagesFilterDocument": docs])
        let page = try await ChatFileSearch.page(filter: .images, cursor: nil, limit: 10) { f, from, l in server.fetch(f, from, l) }
        XCTAssertTrue(page.files.isEmpty)
        XCTAssertLessThanOrEqual(server.requests, ChatFileSearch.maxRequestsPerPage)
        // Continuing eventually reaches the end.
        let ids = try await collectAll(.images, server: server, limit: 10)
        XCTAssertEqual(ids, [])
    }
}

final class SearchTextTests: XCTestCase {
    func testCaseAndTurkishAzerbaijaniFolding() {
        XCTAssertTrue(SearchText.matches("Iğdır Raporu.pdf", query: "igdir"))
        XCTAssertTrue(SearchText.matches("igdir.pdf", query: "IĞDIR"))
        XCTAssertTrue(SearchText.matches("ŞEMA_çizim.PNG", query: "sema cizim"))
        XCTAssertTrue(SearchText.matches("Əli müqavilə.docx", query: "eli muqavile"))
        XCTAssertTrue(SearchText.matches("Öğrenci Listesi.xlsx", query: "liste ogrenci"), "words in any order")
        XCTAssertTrue(SearchText.matches("İstanbul.jpg", query: "istanbul"))
        XCTAssertFalse(SearchText.matches("rapor.pdf", query: "fatura"))
        XCTAssertFalse(SearchText.matches("rapor.pdf", query: "rapor fatura"), "all words must match")
    }

    func testQueryMatchesFileNameAndCaptionLocally() async throws {
        func file(_ id: Int64, _ name: String, caption: String = "") -> ChatFile {
            ChatFile(messageId: id, fileId: Int32(id), kind: .document, fileName: name, size: 1, date: Date(),
                     caption: caption, minithumbnail: nil, thumbnailFileId: nil, isOutgoing: true)
        }
        let all = [file(4, "Kira Sözleşmesi.pdf"), file(3, "notes.txt", caption: "sozlesme taslagi"), file(2, "foto.jpg"), file(1, "SOZLESME-eski.pdf")]
        let page = try await ChatFileSearch.page(filter: .all, query: "sözleşme", cursor: nil, limit: 10) { _, _, _ in (all, 0) }
        XCTAssertEqual(page.files.map(\.messageId), [4, 3, 1])
    }
}
