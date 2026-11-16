import Foundation
@testable import TeleSaveCore

/// Scriptable transport: records sends, hands out message ids, optionally throws.
final class FakeTransport: UploadTransport, @unchecked Sendable {
    private let lock = NSLock()
    private var nextId: Int64

    init(firstMessageId: Int64 = 1001) {
        nextId = firstMessageId - 1
    }
    private(set) var sentBatches: [[OutgoingFile]] = []
    private(set) var cancelled: [Int64] = []
    private(set) var destinations: [UploadDestination?] = []
    /// Errors to throw from successive `send` calls (consumed in order). nil entries succeed.
    var scriptedErrors: [Error?] = []
    var sendDelay: UInt64 = 0

    func send(_ files: [OutgoingFile], to destination: UploadDestination?) async throws -> [SentMessage] {
        if sendDelay > 0 { try? await Task.sleep(nanoseconds: sendDelay) }
        let error: Error? = lock.withLock {
            sentBatches.append(files)
            destinations.append(destination)
            return scriptedErrors.isEmpty ? nil : scriptedErrors.removeFirst()
        }
        if let error { throw error }
        return lock.withLock {
            files.map { _ in
                nextId += 1
                return SentMessage(messageId: nextId, fileId: Int32(nextId))
            }
        }
    }

    func cancel(messageIds: [Int64], in destination: UploadDestination?) async {
        lock.withLock { cancelled.append(contentsOf: messageIds) }
    }

    var sendCount: Int { lock.withLock { sentBatches.count } }
    var allSentPaths: [String] { lock.withLock { sentBatches.flatMap { $0.map(\.path) } } }
}

func makeTempDirectory() -> URL {
    let url = FileManager.default.temporaryDirectory.appendingPathComponent("TeleSaveTests-\(UUID().uuidString)", isDirectory: true)
    try! FileManager.default.createDirectory(at: url, withIntermediateDirectories: true)
    return url
}

@discardableResult
func makeFile(in dir: URL, name: String, bytes: Int) -> URL {
    let url = dir.appendingPathComponent(name)
    FileManager.default.createFile(atPath: url.path, contents: Data(repeating: 0x41, count: bytes))
    return url
}

func prepared(_ url: URL) -> PreparedFile {
    let size = (try? FileManager.default.attributesOfItem(atPath: url.path)[.size] as? NSNumber)?.int64Value ?? 0
    return PreparedFile(url: url, displayName: url.lastPathComponent, size: size, isTemporary: false, bookmark: nil)
}

/// Polls an async condition until true or timeout.
func eventually(timeout: TimeInterval = 2, _ condition: @escaping () async -> Bool) async -> Bool {
    let deadline = Date().addingTimeInterval(timeout)
    while Date() < deadline {
        if await condition() { return true }
        try? await Task.sleep(nanoseconds: 10_000_000)
    }
    return await condition()
}
