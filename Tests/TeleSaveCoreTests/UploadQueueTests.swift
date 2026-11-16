import XCTest
@testable import TeleSaveCore

final class UploadQueueTests: XCTestCase {
    var dir: URL!

    override func setUp() {
        dir = makeTempDirectory()
    }

    override func tearDown() {
        try? FileManager.default.removeItem(at: dir)
    }

    private func states(_ queue: UploadQueue) async -> [UploadItem.State] {
        await queue.items.map(\.state)
    }

    func testStartsPausedAndSendsAfterUnpause() async {
        let transport = FakeTransport()
        let queue = UploadQueue(transport: transport)
        await queue.enqueue([prepared(makeFile(in: dir, name: "a.txt", bytes: 10))])
        try? await Task.sleep(nanoseconds: 50_000_000)
        XCTAssertEqual(transport.sendCount, 0)

        await queue.setPaused(false)
        let sent = await eventually { transport.sendCount == 1 }
        XCTAssertTrue(sent)
        XCTAssertEqual(transport.sentBatches.first?.first?.kind, .document)
    }

    func testSuccessFlowMarksDone() async throws {
        let transport = FakeTransport()
        let queue = UploadQueue(transport: transport)
        await queue.setPaused(false)
        await queue.enqueue([prepared(makeFile(in: dir, name: "a.txt", bytes: 10))])
        let hasMessage = await eventually { await queue.items.first?.messageId != nil }
        XCTAssertTrue(hasMessage)
        let firstMessageId = await queue.items.first?.messageId
        let messageId = try XCTUnwrap(firstMessageId)

        await queue.handle(.fileProgress(fileId: Int32(messageId), uploaded: 5, total: 10))
        guard case .uploading(let p) = await queue.items[0].state else { return XCTFail("not uploading") }
        XCTAssertEqual(p, 0.5, accuracy: 0.001)

        await queue.handle(.sendSucceeded(oldMessageId: messageId, newMessageId: 77))
        let states = await states(queue)
        XCTAssertEqual(states, [.done(messageId: 77)])
    }

    func testConcurrencyLimit() async {
        let transport = FakeTransport()
        let queue = UploadQueue(transport: transport, settings: .init(maxConcurrent: 2))
        await queue.setPaused(false)
        let files = (0..<5).map { prepared(makeFile(in: dir, name: "f\($0).txt", bytes: 10)) }
        await queue.enqueue(files)
        _ = await eventually { transport.sendCount == 2 }
        try? await Task.sleep(nanoseconds: 50_000_000)
        XCTAssertEqual(transport.sendCount, 2, "only two uploads may be in flight")

        // Finish one → next starts.
        let firstId = await queue.items[0].messageId!
        await queue.handle(.sendSucceeded(oldMessageId: firstId, newMessageId: 1))
        let third = await eventually { transport.sendCount == 3 }
        XCTAssertTrue(third)
    }

    func testTransientFailureRetriesThenFails() async {
        let transport = FakeTransport()
        let error = TelegramError(code: 500, message: "Internal")
        transport.scriptedErrors = [error, error, error]
        let policy = RetryPolicy(maxAttempts: 3, baseDelay: 0.01, maxDelay: 0.05)
        let queue = UploadQueue(transport: transport, retryPolicy: policy)
        await queue.setPaused(false)
        await queue.enqueue([prepared(makeFile(in: dir, name: "a.txt", bytes: 10))])

        let failed = await eventually {
            if case .failed = await queue.items[0].state { return true }
            return false
        }
        XCTAssertTrue(failed)
        XCTAssertEqual(transport.sendCount, 3)
        let attempts = await queue.items[0].attempts
        XCTAssertEqual(attempts, 3)
    }

    func testPermanentFailureDoesNotRetry() async {
        let transport = FakeTransport()
        transport.scriptedErrors = [TelegramError(code: 400, message: "FILE_PARTS_INVALID")]
        let queue = UploadQueue(transport: transport, retryPolicy: RetryPolicy(baseDelay: 0.01))
        await queue.setPaused(false)
        await queue.enqueue([prepared(makeFile(in: dir, name: "a.txt", bytes: 10))])
        let failed = await eventually { await queue.items[0].state == .failed(reason: "FILE_PARTS_INVALID") }
        XCTAssertTrue(failed)
        try? await Task.sleep(nanoseconds: 50_000_000)
        XCTAssertEqual(transport.sendCount, 1)
    }

    func testFloodWaitDelaysRetry() async {
        let transport = FakeTransport()
        transport.scriptedErrors = [TelegramError(code: 429, message: "Too Many Requests: retry after 3")]
        // 3s * 0.05 = 0.15s
        let queue = UploadQueue(transport: transport, retryPolicy: RetryPolicy(floodScale: 0.05))
        await queue.setPaused(false)
        await queue.enqueue([prepared(makeFile(in: dir, name: "a.txt", bytes: 10))])
        _ = await eventually { transport.sendCount == 1 }
        try? await Task.sleep(nanoseconds: 60_000_000)
        XCTAssertEqual(transport.sendCount, 1, "must wait for the flood delay")
        let notBefore = await queue.items[0].notBefore
        XCTAssertNotNil(notBefore)
        let retried = await eventually { transport.sendCount == 2 }
        XCTAssertTrue(retried)
    }

    func testSendFailedEventRetries() async {
        let transport = FakeTransport()
        let queue = UploadQueue(transport: transport, retryPolicy: RetryPolicy(baseDelay: 0.01))
        await queue.setPaused(false)
        await queue.enqueue([prepared(makeFile(in: dir, name: "a.txt", bytes: 10))])
        _ = await eventually { await queue.items[0].messageId != nil }
        let mid = await queue.items[0].messageId!
        await queue.handle(.sendFailed(oldMessageId: mid, error: TelegramError(code: 500, message: "boom")))
        let retried = await eventually { transport.sendCount == 2 }
        XCTAssertTrue(retried)
    }

    func testCancelQueuedAndUploading() async {
        let transport = FakeTransport()
        let queue = UploadQueue(transport: transport, settings: .init(maxConcurrent: 1))
        await queue.setPaused(false)
        await queue.enqueue([
            prepared(makeFile(in: dir, name: "a.txt", bytes: 10)),
            prepared(makeFile(in: dir, name: "b.txt", bytes: 10))
        ])
        _ = await eventually { await queue.items[0].messageId != nil }
        let first = await queue.items[0]
        let second = await queue.items[1]
        XCTAssertEqual(second.state, .queued)

        await queue.cancel(second.id)
        await queue.cancel(first.id)
        let afterCancel = await states(queue)
        XCTAssertEqual(afterCancel, [.cancelled, .cancelled])
        XCTAssertEqual(transport.cancelled, [first.messageId!])
        // Late success for a cancelled message is ignored.
        await queue.handle(.sendSucceeded(oldMessageId: first.messageId!, newMessageId: 5))
        let after = await states(queue)
        XCTAssertEqual(after, [.cancelled, .cancelled])
    }

    func testCancelWhileSendInFlightCancelsReturnedMessage() async {
        let transport = FakeTransport()
        transport.sendDelay = 100_000_000
        let queue = UploadQueue(transport: transport)
        await queue.setPaused(false)
        await queue.enqueue([prepared(makeFile(in: dir, name: "a.txt", bytes: 10))])
        _ = await eventually {
            let state = await queue.items[0].state
            return transport.sendCount == 0 && state != .queued
        }
        let id = await queue.items[0].id
        await queue.cancel(id)
        let cancelledRemote = await eventually { transport.cancelled.count == 1 }
        XCTAssertTrue(cancelledRemote)
    }

    func testEarlySuccessEventIsApplied() async {
        // Success update arriving before send() returns must not be lost.
        let transport = FakeTransport()
        let queue = UploadQueue(transport: transport)
        await queue.handle(.sendSucceeded(oldMessageId: 1001, newMessageId: 9))
        await queue.setPaused(false)
        await queue.enqueue([prepared(makeFile(in: dir, name: "a.txt", bytes: 10))])
        let done = await eventually { await queue.items[0].state == .done(messageId: 9) }
        XCTAssertTrue(done)
    }

    func testManualRetryOfFailedItem() async {
        let transport = FakeTransport()
        transport.scriptedErrors = [TelegramError(code: 400, message: "BAD")]
        let queue = UploadQueue(transport: transport)
        await queue.setPaused(false)
        await queue.enqueue([prepared(makeFile(in: dir, name: "a.txt", bytes: 10))])
        _ = await eventually { if case .failed = await queue.items[0].state { return true }; return false }
        await queue.retry(await queue.items[0].id)
        let resent = await eventually { transport.sendCount == 2 }
        XCTAssertTrue(resent)
    }

    func testMissingFileFailsWithoutSending() async {
        let transport = FakeTransport()
        let queue = UploadQueue(transport: transport)
        await queue.setPaused(false)
        let ghost = PreparedFile(url: dir.appendingPathComponent("ghost.txt"), displayName: "ghost.txt", size: 5, isTemporary: false, bookmark: nil)
        await queue.enqueue([ghost])
        let failed = await eventually { if case .failed = await queue.items[0].state { return true }; return false }
        XCTAssertTrue(failed)
        XCTAssertEqual(transport.sendCount, 0)
    }

    func testAlbumGroupingSendsOneBatch() async {
        let transport = FakeTransport()
        let queue = UploadQueue(transport: transport, settings: .init(maxConcurrent: 1, groupAsAlbum: true))
        await queue.setPaused(false)
        let files = (0..<12).map { prepared(makeFile(in: dir, name: "d\($0).pdf", bytes: 10)) }
        await queue.enqueue(files)
        _ = await eventually { transport.sendCount == 1 }
        XCTAssertEqual(transport.sentBatches[0].count, 10, "albums are capped at 10")
        // Finish the album → remaining 2 go as the next album.
        for item in await queue.items.prefix(10) {
            await queue.handle(.sendSucceeded(oldMessageId: item.messageId!, newMessageId: 1))
        }
        let second = await eventually { transport.sendCount == 2 }
        XCTAssertTrue(second)
        XCTAssertEqual(transport.sentBatches[1].count, 2)
    }

    func testPersistenceRestoresUnfinishedAsQueued() async {
        let storeURL = dir.appendingPathComponent("queue.json")
        let store = QueueStore(fileURL: storeURL)
        let transport = FakeTransport()
        let queue = UploadQueue(transport: transport, store: store)
        await queue.setPaused(false)
        await queue.enqueue([prepared(makeFile(in: dir, name: "a.txt", bytes: 10))])
        _ = await eventually { await queue.items[0].messageId != nil }

        let restored = QueueStore(fileURL: storeURL).load()
        XCTAssertEqual(restored.count, 1)
        XCTAssertEqual(restored[0].state, .queued)
        XCTAssertNil(restored[0].messageId)

        let queue2 = UploadQueue(transport: FakeTransport(), store: QueueStore(fileURL: storeURL))
        let count = await queue2.items.count
        XCTAssertEqual(count, 1)
    }

    func testTemporaryFileDeletedAfterSuccess() async {
        let staging = dir.appendingPathComponent("staging/x", isDirectory: true)
        try! FileManager.default.createDirectory(at: staging, withIntermediateDirectories: true)
        let zip = makeFile(in: staging, name: "folder.zip", bytes: 10)
        let transport = FakeTransport()
        let queue = UploadQueue(transport: transport)
        await queue.setPaused(false)
        await queue.enqueue([PreparedFile(url: zip, displayName: "folder.zip", size: 10, isTemporary: true, bookmark: nil)])
        _ = await eventually { await queue.items[0].messageId != nil }
        await queue.handle(.sendSucceeded(oldMessageId: await queue.items[0].messageId!, newMessageId: 3))
        XCTAssertFalse(FileManager.default.fileExists(atPath: staging.path))
    }

    func testClearFinishedKeepsFailedAndActive() async {
        let transport = FakeTransport()
        transport.scriptedErrors = [nil, TelegramError(code: 400, message: "BAD")]
        let queue = UploadQueue(transport: transport, settings: .init(maxConcurrent: 1))
        await queue.setPaused(false)
        await queue.enqueue([
            prepared(makeFile(in: dir, name: "a.txt", bytes: 10)),
            prepared(makeFile(in: dir, name: "b.txt", bytes: 10))
        ])
        _ = await eventually { await queue.items[0].messageId != nil }
        await queue.handle(.sendSucceeded(oldMessageId: await queue.items[0].messageId!, newMessageId: 1))
        _ = await eventually { if case .failed = await queue.items[1].state { return true }; return false }
        await queue.clearFinished()
        let names = await queue.items.map(\.displayName)
        XCTAssertEqual(names, ["b.txt"])
    }

    func testCaptionAndMediaKindApplied() async {
        let transport = FakeTransport()
        let settings = UploadQueue.Settings(sendMediaCompressed: true, caption: CaptionTemplate("{filename}"))
        let queue = UploadQueue(transport: transport, settings: settings, probe: { _, _ in MediaDimensions(width: 4, height: 3) })
        await queue.setPaused(false)
        await queue.enqueue([prepared(makeFile(in: dir, name: "pic.jpg", bytes: 10))])
        _ = await eventually { transport.sendCount == 1 }
        let sent = transport.sentBatches[0][0]
        XCTAssertEqual(sent.kind, .photo)
        XCTAssertEqual(sent.caption, "pic.jpg")
        XCTAssertEqual(sent.dimensions, MediaDimensions(width: 4, height: 3))
    }

    // MARK: - Destinations

    func testDestinationPassedToTransport() async {
        let transport = FakeTransport()
        let queue = UploadQueue(transport: transport)
        await queue.setPaused(false)
        let group = UploadDestination(chatId: -100123, forumTopicId: 7, title: "Ekip", username: nil)
        await queue.enqueue([prepared(makeFile(in: dir, name: "a.txt", bytes: 10))], to: group)
        await queue.enqueue([prepared(makeFile(in: dir, name: "b.txt", bytes: 10))])
        _ = await eventually { transport.sendCount == 2 }
        XCTAssertEqual(transport.destinations.count, 2)
        XCTAssertEqual(transport.destinations[0], group)
        XCTAssertNil(transport.destinations[1] ?? nil, "no destination = Saved Messages")
    }

    func testSavedMessagesDestinationStoredAsNil() async {
        let queue = UploadQueue(transport: FakeTransport())
        await queue.enqueue([prepared(makeFile(in: dir, name: "a.txt", bytes: 10))], to: .savedMessages)
        let destination = await queue.items[0].destination
        XCTAssertNil(destination)
    }

    func testAlbumsNeverMixDestinations() async {
        let transport = FakeTransport()
        let queue = UploadQueue(transport: transport, settings: .init(maxConcurrent: 1, groupAsAlbum: true))
        let channel = UploadDestination(chatId: -100999, title: "Kanal")
        await queue.enqueue((0..<2).map { prepared(makeFile(in: dir, name: "a\($0).pdf", bytes: 10)) })
        await queue.enqueue((0..<2).map { prepared(makeFile(in: dir, name: "b\($0).pdf", bytes: 10)) }, to: channel)
        let groups = await queue.items.map(\.groupId)
        XCTAssertNotEqual(groups[0], groups[2])
        await queue.setPaused(false)
        _ = await eventually { transport.sendCount == 1 }
        XCTAssertEqual(transport.sentBatches[0].count, 2)
        XCTAssertNil(transport.destinations[0] ?? nil)
    }

    func testDestinationPersistsAndOldQueueFilesDecode() async throws {
        let storeURL = dir.appendingPathComponent("queue.json")
        let queue = UploadQueue(transport: FakeTransport(), store: QueueStore(fileURL: storeURL))
        let channel = UploadDestination(chatId: -100999, forumTopicId: 3, title: "Kanal", username: "kanal")
        await queue.enqueue([prepared(makeFile(in: dir, name: "a.txt", bytes: 10))], to: channel)
        XCTAssertEqual(QueueStore(fileURL: storeURL).load().first?.destination, channel)

        // A queue file written before destinations existed has no "destination" key.
        var json = try JSONSerialization.jsonObject(with: Data(contentsOf: storeURL)) as! [[String: Any]]
        json[0]["destination"] = nil
        try JSONSerialization.data(withJSONObject: json).write(to: storeURL)
        let legacy = QueueStore(fileURL: storeURL).load()
        XCTAssertEqual(legacy.count, 1)
        XCTAssertNil(legacy[0].destination)
    }

    func testCancelUsesItemDestination() async {
        let transport = FakeTransport()
        let queue = UploadQueue(transport: transport)
        await queue.setPaused(false)
        await queue.enqueue([prepared(makeFile(in: dir, name: "a.txt", bytes: 10))], to: UploadDestination(chatId: -5, title: "G"))
        _ = await eventually { await queue.items[0].messageId != nil }
        await queue.cancel(await queue.items[0].id)
        XCTAssertEqual(transport.cancelled.count, 1)
    }

    func testDueRetryWaitsForFreeSlotWithoutSpinning() async {
        // Slot 1 busy with a.txt; b.txt fails and becomes due while the slot is still taken.
        let transport = FakeTransport()
        transport.scriptedErrors = [nil, TelegramError(code: 500, message: "x")]
        let queue = UploadQueue(transport: transport, settings: .init(maxConcurrent: 1), retryPolicy: RetryPolicy(baseDelay: 0))
        await queue.setPaused(false)
        await queue.enqueue([prepared(makeFile(in: dir, name: "a.txt", bytes: 10))])
        _ = await eventually { await queue.items[0].messageId != nil }
        await queue.setPaused(true)
        await queue.enqueue([prepared(makeFile(in: dir, name: "b.txt", bytes: 10))])
        await queue.setPaused(false)
        try? await Task.sleep(nanoseconds: 100_000_000)
        XCTAssertEqual(transport.sendCount, 1, "b waits while a holds the only slot")

        await queue.handle(.sendSucceeded(oldMessageId: await queue.items[0].messageId!, newMessageId: 1))
        let bSent = await eventually { transport.sendCount >= 2 }
        XCTAssertTrue(bSent)
    }
}
