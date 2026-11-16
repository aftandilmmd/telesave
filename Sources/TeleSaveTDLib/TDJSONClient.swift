import Foundation
import TDLibFramework
import TeleSaveCore

typealias TDObject = [String: Any]

/// `td_receive` is process-global: one thread pulls every response/update and routes it by `@client_id`.
final class TDReceiver: @unchecked Sendable {
    static let shared = TDReceiver()

    private let lock = NSLock()
    private var handlers: [Int: (TDObject) -> Void] = [:]
    private var thread: Thread?

    func register(clientId: Int, handler: @escaping (TDObject) -> Void) {
        lock.lock()
        handlers[clientId] = handler
        let needsThread = thread == nil
        if needsThread {
            let t = Thread { [unowned self] in self.loop() }
            t.name = "TeleSave.TDReceiver"
            t.qualityOfService = .userInitiated
            thread = t
            t.start()
        }
        lock.unlock()
    }

    func unregister(clientId: Int) {
        lock.lock()
        handlers[clientId] = nil
        lock.unlock()
    }

    private func loop() {
        while true {
            guard let raw = td_receive(1.0) else { continue }
            let data = Data(bytes: raw, count: strlen(raw))
            guard let object = (try? JSONSerialization.jsonObject(with: data)) as? TDObject,
                  let clientId = (object["@client_id"] as? NSNumber)?.intValue else { continue }
            lock.lock()
            let handler = handlers[clientId]
            lock.unlock()
            handler?(object)
        }
    }
}

/// One TDLib instance speaking the JSON interface. Requests get an `@extra` tag so responses
/// resume the awaiting caller; everything else is an update passed to `onUpdate`.
final class TDJSONClient: @unchecked Sendable {
    let clientId: Int32

    private let lock = NSLock()
    private var nextExtra = 1
    private var pending: [String: CheckedContinuation<TDObject, Error>] = [:]
    private let onUpdate: (TDObject) -> Void

    init(onUpdate: @escaping (TDObject) -> Void) {
        self.clientId = td_create_client_id()
        self.onUpdate = onUpdate
        TDReceiver.shared.register(clientId: Int(clientId)) { [weak self] object in
            self?.dispatch(object)
        }
    }

    deinit {
        TDReceiver.shared.unregister(clientId: Int(clientId))
    }

    /// Fire-and-forget request.
    func send(_ request: TDObject) {
        _ = transmit(request)
    }

    func request(_ request: TDObject) async throws -> TDObject {
        try await withCheckedThrowingContinuation { continuation in
            lock.lock()
            let extra = "ts\(nextExtra)"
            nextExtra += 1
            pending[extra] = continuation
            lock.unlock()

            var tagged = request
            tagged["@extra"] = extra
            if !transmit(tagged) {
                lock.lock()
                let cont = pending.removeValue(forKey: extra)
                lock.unlock()
                cont?.resume(throwing: TelegramError(code: 400, message: "Invalid request"))
            }
        }
    }

    /// Fails every outstanding request (used when the instance closes).
    func failPending(_ error: Error) {
        lock.lock()
        let all = pending
        pending.removeAll()
        lock.unlock()
        all.values.forEach { $0.resume(throwing: error) }
    }

    static func execute(_ request: TDObject) {
        guard let data = try? JSONSerialization.data(withJSONObject: request),
              let json = String(data: data, encoding: .utf8) else { return }
        _ = json.withCString { td_execute($0) }
    }

    private func transmit(_ request: TDObject) -> Bool {
        guard let data = try? JSONSerialization.data(withJSONObject: request),
              let json = String(data: data, encoding: .utf8) else { return false }
        json.withCString { td_send(clientId, $0) }
        return true
    }

    private func dispatch(_ object: TDObject) {
        if let extra = object["@extra"] as? String {
            lock.lock()
            let continuation = pending.removeValue(forKey: extra)
            lock.unlock()
            if let continuation {
                if object["@type"] as? String == "error" {
                    continuation.resume(throwing: TelegramError(
                        code: (object["code"] as? NSNumber)?.intValue ?? 0,
                        message: object["message"] as? String ?? "Unknown error"
                    ))
                } else {
                    continuation.resume(returning: object)
                }
                return
            }
        }
        onUpdate(object)
    }
}

extension Dictionary where Key == String, Value == Any {
    func int64(_ key: String) -> Int64? {
        if let n = self[key] as? NSNumber { return n.int64Value }
        if let s = self[key] as? String { return Int64(s) }
        return nil
    }

    func object(_ key: String) -> TDObject? {
        self[key] as? TDObject
    }
}
