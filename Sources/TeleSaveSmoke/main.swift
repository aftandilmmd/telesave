import Foundation
import TeleSaveCore
import TeleSaveTDLib

// Verifies the TDLib integration without a Telegram account: a fresh client must accept our
// setTdlibParameters and reach authorizationStateWaitPhoneNumber.
// Usage: TeleSaveSmoke [api_id api_hash]   (dummy credentials are fine for this check)

let args = CommandLine.arguments
let credentials = ApiCredentials(
    apiId: args.count > 2 ? Int(args[1]) ?? 1 : 1,
    apiHash: args.count > 2 ? args[2] : String(repeating: "0", count: 32)
)
let root = FileManager.default.temporaryDirectory.appendingPathComponent("TeleSaveSmoke-\(UUID().uuidString)")
defer { try? FileManager.default.removeItem(at: root) }

let reached = DispatchSemaphore(value: 0)
let client = TDLibTelegramClient(
    configuration: .init(
        databaseDirectory: root.appendingPathComponent("db"),
        filesDirectory: root.appendingPathComponent("files"),
        logFile: root.appendingPathComponent("tdlib.log"),
        credentials: credentials,
        encryptionKey: Data(repeating: 7, count: 32),
        applicationVersion: "smoke"
    )
) { event in
    print("event:", event)
    if case .auth(.waitPhoneNumber) = event { reached.signal() }
}
client.start()
if reached.wait(timeout: .now() + 30) == .timedOut {
    print("FAIL: did not reach waitPhoneNumber within 30s")
    if let log = try? String(contentsOf: root.appendingPathComponent("tdlib.log"), encoding: .utf8) { print(log.suffix(2000)) }
    exit(1)
}
print("OK: TDLib initialised and is waiting for a phone number")

// One real server round-trip: request a QR login. Dummy credentials must be rejected by Telegram
// (API_ID_INVALID); real ones move to waitQRConfirmation.
let roundTrip = DispatchSemaphore(value: 0)
nonisolated(unsafe) var outcome = "timeout"
Task {
    do {
        try await client.requestQRCode()
        outcome = "accepted"
    } catch {
        outcome = "error: \(error)"
    }
    roundTrip.signal()
}
_ = roundTrip.wait(timeout: .now() + 30)
print("requestQRCode →", outcome)
client.close()

// The same check the app runs before replacing an API key.
let validation = DispatchSemaphore(value: 0)
nonisolated(unsafe) var validatorOutcome = ApiKeyValidator.Outcome.failed("not run")
Task {
    validatorOutcome = await ApiKeyValidator.validate(credentials)
    validation.signal()
}
_ = validation.wait(timeout: .now() + 40)
print("ApiKeyValidator →", validatorOutcome)
exit(outcome == "timeout" ? 1 : 0)
