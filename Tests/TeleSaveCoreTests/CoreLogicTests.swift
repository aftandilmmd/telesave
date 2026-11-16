import XCTest
@testable import TeleSaveCore

final class DropIntakeTests: XCTestCase {
    var dir: URL!
    var staging: URL!

    override func setUp() {
        dir = makeTempDirectory()
        staging = dir.appendingPathComponent("staging", isDirectory: true)
    }

    override func tearDown() {
        try? FileManager.default.removeItem(at: dir)
    }

    func testAcceptsFileAndRejectsEmptyMissingAndTooLarge() async {
        let intake = DropIntake(stagingDirectory: staging)
        let ok = makeFile(in: dir, name: "ok.txt", bytes: 10)
        let empty = makeFile(in: dir, name: "empty.txt", bytes: 0)
        let big = makeFile(in: dir, name: "big.bin", bytes: 200)
        let missing = dir.appendingPathComponent("missing.txt")

        let result = await intake.prepare([ok, empty, big, missing], maxFileSize: 100)
        XCTAssertEqual(result.accepted.map(\.displayName), ["ok.txt"])
        XCTAssertEqual(result.accepted.first?.size, 10)
        XCTAssertEqual(result.rejected.map(\.reason), [.empty, .tooLarge(limit: 100), .unreadable])
    }

    func testZipsFolders() async throws {
        let intake = DropIntake(stagingDirectory: staging)
        let folder = dir.appendingPathComponent("Photos", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        makeFile(in: folder, name: "1.txt", bytes: 100)
        makeFile(in: folder, name: "2.txt", bytes: 100)

        let result = await intake.prepare([folder], maxFileSize: 1_000_000)
        let zip = try XCTUnwrap(result.accepted.first)
        XCTAssertEqual(zip.displayName, "Photos.zip")
        XCTAssertTrue(zip.isTemporary)
        XCTAssertTrue(zip.url.path.hasPrefix(staging.path))
        let header = try Data(contentsOf: zip.url).prefix(2)
        XCTAssertEqual(Array(header), [0x50, 0x4B], "zip magic 'PK'")
    }

    func testRejectsRecentDuplicate() async {
        let intake = DropIntake(stagingDirectory: staging, duplicateWindow: 60)
        let file = makeFile(in: dir, name: "a.txt", bytes: 10)
        let first = await intake.prepare([file], maxFileSize: 100)
        let second = await intake.prepare([file], maxFileSize: 100)
        XCTAssertEqual(first.accepted.count, 1)
        XCTAssertEqual(second.rejected.map(\.reason), [.duplicate])
    }

    func testDuplicateAllowedAfterWindow() async {
        final class Clock: @unchecked Sendable { var now = Date() }
        let clock = Clock()
        let intake = DropIntake(stagingDirectory: staging, duplicateWindow: 60, now: { clock.now })
        let file = makeFile(in: dir, name: "a.txt", bytes: 10)
        _ = await intake.prepare([file], maxFileSize: 100)
        clock.now = clock.now.addingTimeInterval(61)
        let again = await intake.prepare([file], maxFileSize: 100)
        XCTAssertEqual(again.accepted.count, 1)
    }
}

final class FolderModeTests: XCTestCase {
    var dir: URL!

    override func setUp() { dir = makeTempDirectory() }
    override func tearDown() { try? FileManager.default.removeItem(at: dir) }

    private func makeFolder() throws -> URL {
        let folder = dir.appendingPathComponent("Trip", isDirectory: true)
        let nested = folder.appendingPathComponent("Day 2", isDirectory: true)
        try FileManager.default.createDirectory(at: nested, withIntermediateDirectories: true)
        makeFile(in: folder, name: "b.jpg", bytes: 10)
        makeFile(in: folder, name: "a.jpg", bytes: 10)
        makeFile(in: folder, name: ".DS_Store", bytes: 10)
        makeFile(in: nested, name: "c.mov", bytes: 10)
        let app = folder.appendingPathComponent("Tool.app/Contents", isDirectory: true)
        try FileManager.default.createDirectory(at: app, withIntermediateDirectories: true)
        makeFile(in: app, name: "Info.plist", bytes: 10)
        return folder
    }

    func testExpandSendsFilesInsideSkippingHiddenAndZippingPackages() async throws {
        let intake = DropIntake(stagingDirectory: dir.appendingPathComponent("staging"))
        let result = await intake.prepare([try makeFolder()], maxFileSize: 1_000_000, folderMode: .expand)
        XCTAssertEqual(result.rejected, [])
        XCTAssertEqual(result.accepted.map(\.displayName), ["a.jpg", "b.jpg", "c.mov", "Tool.app.zip"])
        XCTAssertEqual(result.accepted.filter(\.isTemporary).map(\.displayName), ["Tool.app.zip"])
    }

    func testZipModeSendsOneArchive() async throws {
        let intake = DropIntake(stagingDirectory: dir.appendingPathComponent("staging"))
        let result = await intake.prepare([try makeFolder()], maxFileSize: 1_000_000, folderMode: .zip)
        XCTAssertEqual(result.accepted.map(\.displayName), ["Trip.zip"])
    }

    func testExpandRejectsEmptyAndHugeFolders() async throws {
        let intake = DropIntake(stagingDirectory: dir.appendingPathComponent("staging"))
        let empty = dir.appendingPathComponent("Empty", isDirectory: true)
        try FileManager.default.createDirectory(at: empty, withIntermediateDirectories: true)
        makeFile(in: empty, name: ".hidden", bytes: 1)
        let huge = dir.appendingPathComponent("Huge", isDirectory: true)
        try FileManager.default.createDirectory(at: huge, withIntermediateDirectories: true)
        for i in 0...DropIntake.maxFilesPerFolder { makeFile(in: huge, name: "\(i).txt", bytes: 1) }

        let result = await intake.prepare([empty, huge], maxFileSize: 1_000_000, folderMode: .expand)
        XCTAssertEqual(result.accepted, [])
        XCTAssertEqual(result.rejected.map(\.reason), [.empty, .tooManyFiles(count: DropIntake.maxFilesPerFolder + 1)])
    }

    func testTemporaryFlagForPastedFiles() async {
        let intake = DropIntake(stagingDirectory: dir.appendingPathComponent("staging"))
        let pasted = makeFile(in: dir, name: "Pasted.png", bytes: 10)
        let result = await intake.prepare([pasted], maxFileSize: 100, temporary: true)
        XCTAssertEqual(result.accepted.first?.isTemporary, true)
        XCTAssertNil(result.accepted.first?.bookmark)
    }
}

final class AuthStateTests: XCTestCase {
    func testMapsStates() {
        XCTAssertEqual(AuthState(tdObject: ["@type": "authorizationStateWaitPhoneNumber"]), .waitPhoneNumber)
        XCTAssertEqual(AuthState(tdObject: ["@type": "authorizationStateReady"]), .ready)
        XCTAssertEqual(AuthState(tdObject: ["@type": "authorizationStateClosed"]), .closed)
        XCTAssertEqual(
            AuthState(tdObject: ["@type": "authorizationStateWaitOtherDeviceConfirmation", "link": "tg://login?token=abc"]),
            .waitQRConfirmation(link: "tg://login?token=abc")
        )
        XCTAssertEqual(
            AuthState(tdObject: ["@type": "authorizationStateWaitPassword", "password_hint": "kedi"]),
            .waitPassword(hint: "kedi")
        )
        XCTAssertEqual(
            AuthState(tdObject: [
                "@type": "authorizationStateWaitCode",
                "code_info": ["phone_number": "+905551112233", "type": ["@type": "authenticationCodeTypeTelegramMessage"]]
            ]),
            .waitCode(phoneNumber: "+905551112233", delivery: .telegramApp)
        )
    }

    func testIgnoresParametersStateAndUnknown() {
        XCTAssertNil(AuthState(tdObject: ["@type": "authorizationStateWaitTdlibParameters"]))
        XCTAssertNil(AuthState(tdObject: ["@type": "somethingNew"]))
        XCTAssertNil(AuthState(tdObject: [:]))
    }

    func testConnectionState() {
        XCTAssertEqual(ConnectionState(tdObject: ["@type": "connectionStateReady"]), .ready)
        XCTAssertEqual(ConnectionState(tdObject: ["@type": "connectionStateWaitingForNetwork"]), .waitingForNetwork)
    }

    func testErrorText() {
        L10n.selectedCode = "tr"
        defer { L10n.selectedCode = L10n.systemCode }
        XCTAssertEqual(AuthErrorText.describe(TelegramError(code: 400, message: "PHONE_CODE_INVALID")), "Kod hatalı.")
        XCTAssertTrue(AuthErrorText.describe(TelegramError(code: 429, message: "Too Many Requests: retry after 42")).contains("42"))
    }
}

final class PolicyTests: XCTestCase {
    func testErrorClassification() {
        XCTAssertEqual(TelegramError(code: 429, message: "Too Many Requests: retry after 17").kind, .flood(seconds: 17))
        XCTAssertEqual(TelegramError(code: 420, message: "FLOOD_WAIT_9").kind, .flood(seconds: 9))
        XCTAssertEqual(TelegramError(code: 500, message: "Internal").kind, .transient)
        XCTAssertEqual(TelegramError(code: 400, message: "Request Timeout").kind, .transient)
        XCTAssertEqual(TelegramError(code: 400, message: "FILE_PARTS_INVALID").kind, .permanent)
    }

    func testRetryBackoff() {
        let policy = RetryPolicy(maxAttempts: 4, baseDelay: 2, maxDelay: 5)
        let transient = TelegramError(code: 500, message: "x")
        XCTAssertEqual(policy.delay(after: transient, attempts: 1), 2)
        XCTAssertEqual(policy.delay(after: transient, attempts: 2), 4)
        XCTAssertEqual(policy.delay(after: transient, attempts: 3), 5, "capped")
        XCTAssertNil(policy.delay(after: transient, attempts: 4))
        XCTAssertNil(policy.delay(after: TelegramError(code: 400, message: "BAD"), attempts: 1))
        XCTAssertEqual(policy.delay(after: TelegramError(code: 429, message: "retry after 30"), attempts: 6), 30)
    }

    func testMediaClassifier() {
        XCTAssertEqual(MediaClassifier.kind(forPath: "/a.jpg", size: 100, sendMediaCompressed: false), .document)
        XCTAssertEqual(MediaClassifier.kind(forPath: "/a.jpg", size: 100, sendMediaCompressed: true), .photo)
        XCTAssertEqual(MediaClassifier.kind(forPath: "/a.png", size: 20 * 1024 * 1024, sendMediaCompressed: true), .document)
        XCTAssertEqual(MediaClassifier.kind(forPath: "/a.mov", size: 100, sendMediaCompressed: true), .video)
        XCTAssertEqual(MediaClassifier.kind(forPath: "/a.mp4", size: 100, sendMediaCompressed: true), .video)
        XCTAssertEqual(MediaClassifier.kind(forPath: "/a.pdf", size: 100, sendMediaCompressed: true), .document)
    }

    func testAlbumGroupingSeparatesFamilies() {
        func file(_ name: String) -> PreparedFile {
            PreparedFile(url: URL(fileURLWithPath: "/tmp/\(name)"), displayName: name, size: 10, isTemporary: false, bookmark: nil)
        }
        let files = [file("1.jpg"), file("2.pdf"), file("3.mp4"), file("4.pdf")]
        let grouped = AlbumGrouper.assignGroups(files, sendMediaCompressed: true)
        let groups = Dictionary(grouping: grouped, by: { $0.1 })
        XCTAssertEqual(groups.count, 2)
        for (_, members) in groups {
            let kinds = Set(members.map { MediaClassifier.kind(forPath: $0.0.url.path, size: 10, sendMediaCompressed: true) == .document })
            XCTAssertEqual(kinds.count, 1, "an album must not mix documents with media")
        }
        // A single file gets no group.
        XCTAssertNil(AlbumGrouper.assignGroups([file("x.pdf")], sendMediaCompressed: false)[0].1)
    }

    func testCaptionTemplate() {
        let date = Date(timeIntervalSince1970: 0)
        XCTAssertEqual(CaptionTemplate("").render(fileName: "a", size: 1, date: date), "")
        XCTAssertEqual(CaptionTemplate("#telesave {filename}").render(fileName: "a.txt", size: 1, date: date), "#telesave a.txt")
        XCTAssertFalse(CaptionTemplate("{size}").render(fileName: "a", size: 2048, date: date).contains("{size}"))
    }

    func testObfuscatorRoundTrip() {
        let hash = "0123456789abcdef0123456789abcdef"
        let encoded = Obfuscator.encode(hash)
        XCTAssertNotEqual(encoded, hash)
        XCTAssertEqual(Obfuscator.decode(encoded), hash)
    }

    func testCredentialsValidation() {
        XCTAssertTrue(ApiCredentials(apiId: 12345, apiHash: "0123456789abcdef0123456789abcdef").isValid)
        XCTAssertFalse(ApiCredentials(apiId: 0, apiHash: "0123456789abcdef0123456789abcdef").isValid)
        XCTAssertFalse(ApiCredentials(apiId: 1, apiHash: "short").isValid)
    }

    func testCredentialsFromEnvironment() {
        let keychain = KeychainStore(service: "com.telesave.tests.\(UUID().uuidString)")
        let env = ["TELESAVE_API_ID": "123", "TELESAVE_API_HASH": "0123456789abcdef0123456789abcdef"]
        XCTAssertEqual(CredentialsResolver.resolve(keychain: keychain, bundle: Bundle(for: Self.self), environment: env)?.apiId, 123)
        XCTAssertNil(CredentialsResolver.resolve(keychain: keychain, bundle: Bundle(for: Self.self), environment: [:]))
    }

    func testNoBundledCredentialsInTestBundle() {
        XCTAssertNil(CredentialsResolver.bundled(in: Bundle(for: Self.self)))
    }

    func testTransferRate() {
        var rate = TransferRate(smoothing: 1)
        let t0 = Date()
        rate.sample(uploadedBytes: 0, at: t0)
        rate.sample(uploadedBytes: 100, at: t0.addingTimeInterval(0.1)) // too soon, baseline kept
        rate.sample(uploadedBytes: 1000, at: t0.addingTimeInterval(1))
        XCTAssertEqual(rate.bytesPerSecond, 1000, accuracy: 0.1)
        XCTAssertEqual(rate.eta(remainingBytes: 5000) ?? 0, 5, accuracy: 0.01)
    }

    func testUserUploadLimit() {
        XCTAssertEqual(TelegramUser(id: 1, firstName: "", lastName: "", username: nil, isPremium: false).maxUploadSize, 2000 * 1024 * 1024)
        XCTAssertEqual(TelegramUser(id: 1, firstName: "", lastName: "", username: nil, isPremium: true).maxUploadSize, 4000 * 1024 * 1024)
    }
}

final class ChatPermissionRulesTests: XCTestCase {
    private func can(_ status: [String: Any]?, channel: Bool, defaults: Bool = true) -> Bool {
        ChatPermissionRules.canSendFiles(status: status, isChannel: channel, defaultPermission: defaults)
    }

    func testChannel() {
        XCTAssertTrue(can(["@type": "chatMemberStatusCreator"], channel: true))
        XCTAssertTrue(can(["@type": "chatMemberStatusAdministrator", "rights": ["can_post_messages": true]], channel: true))
        XCTAssertFalse(can(["@type": "chatMemberStatusAdministrator", "rights": ["can_post_messages": false]], channel: true))
        XCTAssertFalse(can(["@type": "chatMemberStatusMember"], channel: true), "subscribers cannot post")
    }

    func testGroup() {
        XCTAssertTrue(can(["@type": "chatMemberStatusAdministrator", "rights": [:]], channel: false))
        XCTAssertTrue(can(["@type": "chatMemberStatusMember"], channel: false, defaults: true))
        XCTAssertFalse(can(["@type": "chatMemberStatusMember"], channel: false, defaults: false), "documents disabled for members")
        XCTAssertTrue(can(["@type": "chatMemberStatusRestricted", "permissions": ["can_send_documents": true]], channel: false, defaults: false))
        XCTAssertFalse(can(["@type": "chatMemberStatusRestricted", "permissions": ["can_send_documents": false]], channel: false))
        XCTAssertFalse(can(["@type": "chatMemberStatusLeft"], channel: false))
        XCTAssertFalse(can(["@type": "chatMemberStatusBanned"], channel: false))
        XCTAssertFalse(can(nil, channel: false))
    }
}

final class MessageFileLocatorTests: XCTestCase {
    func testPicksMainFile() {
        XCTAssertEqual(MessageFileLocator.primaryFileId(content: [
            "@type": "messageDocument", "document": ["thumbnail": ["file": ["id": 1]], "document": ["id": 2]]
        ]), 2)
        XCTAssertEqual(MessageFileLocator.primaryFileId(content: [
            "@type": "messagePhoto",
            "photo": ["sizes": [
                ["width": 90, "height": 60, "photo": ["id": 10]],
                ["width": 1280, "height": 853, "photo": ["id": 12]],
                ["width": 320, "height": 213, "photo": ["id": 11]]
            ]]
        ]), 12, "largest size, not the first")
        XCTAssertEqual(MessageFileLocator.primaryFileId(content: [
            "@type": "messageVideo", "video": ["thumbnail": ["file": ["id": 20]], "video": ["id": 21]]
        ]), 21)
        XCTAssertNil(MessageFileLocator.primaryFileId(content: ["@type": "messageText"]))
    }

    func testUniqueURL() throws {
        let dir = makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        XCTAssertEqual(MessageFileLocator.uniqueURL(in: dir, fileName: "a.pdf").lastPathComponent, "a.pdf")
        makeFile(in: dir, name: "a.pdf", bytes: 1)
        makeFile(in: dir, name: "a (2).pdf", bytes: 1)
        XCTAssertEqual(MessageFileLocator.uniqueURL(in: dir, fileName: "a.pdf").lastPathComponent, "a (3).pdf")
        makeFile(in: dir, name: "README", bytes: 1)
        XCTAssertEqual(MessageFileLocator.uniqueURL(in: dir, fileName: "README").lastPathComponent, "README (2)")
    }
}
