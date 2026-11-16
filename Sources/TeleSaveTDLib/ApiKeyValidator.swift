import Foundation
import TeleSaveCore

/// Checks an api_id / api_hash with Telegram before it replaces the current key.
///
/// Uses a throwaway TDLib instance in a temporary directory: once it is ready for login it asks for a
/// QR login token. Telegram answers API_ID_INVALID for a bad key; any other answer means the key works.
/// The running client and its session are not touched.
public enum ApiKeyValidator {
    public enum Outcome: Equatable, Sendable {
        case valid
        case invalid
        case failed(String)
    }

    public static func validate(_ credentials: ApiCredentials, timeout: TimeInterval = 25) async -> Outcome {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("TeleSaveKeyCheck-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: root) }

        let (events, continuation) = AsyncStream.makeStream(of: AuthState.self)
        let client = TDLibTelegramClient(
            configuration: .init(
                databaseDirectory: root.appendingPathComponent("db"),
                filesDirectory: root.appendingPathComponent("files"),
                logFile: nil,
                credentials: credentials,
                encryptionKey: Data((0..<32).map { _ in UInt8.random(in: 0...255) }),
                applicationVersion: "key-check"
            )
        ) { event in
            if case .auth(let state) = event { continuation.yield(state) }
        }
        client.start()
        defer {
            continuation.finish()
            // close() waits for TDLib to shut down; keep it off the caller's thread.
            Thread.detachNewThread { client.close() }
        }

        // First result wins; the deferred close() then also releases a request still waiting on TDLib.
        return await withCheckedContinuation { (result: CheckedContinuation<Outcome, Never>) in
            let once = Once()
            let check = Task {
                var outcome = Outcome.failed(L("Zaman aşımı"))
                for await state in events where state == .waitPhoneNumber {
                    do {
                        try await client.requestQRCode()
                        outcome = .valid
                    } catch let error as TelegramError where error.message.hasPrefix("API_ID") {
                        outcome = .invalid
                    } catch {
                        outcome = .failed(error.localizedDescription)
                    }
                    break
                }
                if once.claim() { result.resume(returning: outcome) }
            }
            Task {
                try? await Task.sleep(nanoseconds: UInt64(timeout * 1_000_000_000))
                if once.claim() {
                    check.cancel()
                    result.resume(returning: .failed(L("Zaman aşımı")))
                }
            }
        }
    }

}

/// Lets exactly one of several racing tasks resume a continuation.
private final class Once: @unchecked Sendable {
    private let lock = NSLock()
    private var done = false

    func claim() -> Bool {
        lock.withLock {
            defer { done = true }
            return !done
        }
    }
}
