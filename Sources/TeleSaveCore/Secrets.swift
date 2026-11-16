import Foundation
import Security

/// Light obfuscation for the bundled api_hash so it is not a plain string in the binary/Info.plist.
/// This is not encryption: every Telegram client ships its api_hash; this only stops casual scraping.
public enum Obfuscator {
    static let key: [UInt8] = Array("TeleSave.obfuscation.v1".utf8)

    public static func encode(_ plain: String) -> String {
        Data(xor(Array(plain.utf8))).base64EncodedString()
    }

    public static func decode(_ encoded: String) -> String? {
        guard let data = Data(base64Encoded: encoded) else { return nil }
        return String(bytes: xor(Array(data)), encoding: .utf8)
    }

    private static func xor(_ bytes: [UInt8]) -> [UInt8] {
        bytes.enumerated().map { $0.element ^ key[$0.offset % key.count] }
    }
}

public final class KeychainStore: @unchecked Sendable {
    private let service: String

    public init(service: String) {
        self.service = service
    }

    public func data(for account: String) -> Data? {
        var query = baseQuery(account)
        query[kSecReturnData as String] = true
        query[kSecMatchLimit as String] = kSecMatchLimitOne
        var result: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess else { return nil }
        return result as? Data
    }

    @discardableResult
    public func set(_ data: Data, for account: String) -> Bool {
        let query = baseQuery(account)
        let update = [kSecValueData as String: data]
        let status = SecItemUpdate(query as CFDictionary, update as CFDictionary)
        if status == errSecItemNotFound {
            var add = query
            add[kSecValueData as String] = data
            add[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlockThisDeviceOnly
            return SecItemAdd(add as CFDictionary, nil) == errSecSuccess
        }
        return status == errSecSuccess
    }

    public func delete(_ account: String) {
        SecItemDelete(baseQuery(account) as CFDictionary)
    }

    private func baseQuery(_ account: String) -> [String: Any] {
        [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account
        ]
    }
}

/// Resolves api_id/api_hash: user-entered (Keychain) → bundled (Info.plist) → environment (development).
public enum CredentialsResolver {
    public static let keychainAccount = "api-credentials"

    /// Order: user-entered (only offered in builds without a bundled key) → remote config → bundled → environment.
    public static func resolve(keychain: KeychainStore, remote: ApiCredentials? = nil, bundle: Bundle = .main, environment: [String: String] = ProcessInfo.processInfo.environment) -> ApiCredentials? {
        if let data = keychain.data(for: keychainAccount),
           let saved = try? JSONDecoder().decode(ApiCredentials.self, from: data), saved.isValid {
            return saved
        }
        if let remote, remote.isValid { return remote }
        if let bundled = bundled(in: bundle) { return bundled }
        if let idString = environment["TELESAVE_API_ID"], let id = Int(idString), let hash = environment["TELESAVE_API_HASH"] {
            let env = ApiCredentials(apiId: id, apiHash: hash)
            if env.isValid { return env }
        }
        return nil
    }

    /// API key baked into Info.plist at build time (Config/Secrets.env), if any.
    public static func bundled(in bundle: Bundle = .main) -> ApiCredentials? {
        guard let idString = bundle.object(forInfoDictionaryKey: "TSApiID") as? String, let id = Int(idString),
              let encoded = bundle.object(forInfoDictionaryKey: "TSApiHash") as? String,
              let hash = Obfuscator.decode(encoded) else { return nil }
        let credentials = ApiCredentials(apiId: id, apiHash: hash)
        return credentials.isValid ? credentials : nil
    }

    public static func save(_ credentials: ApiCredentials, keychain: KeychainStore) -> Bool {
        guard let data = try? JSONEncoder().encode(credentials) else { return false }
        return keychain.set(data, for: keychainAccount)
    }
}
