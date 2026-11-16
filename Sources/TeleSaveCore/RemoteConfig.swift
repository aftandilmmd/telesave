import CryptoKit
import Foundation

/// Remotely managed settings: lets the API key be rotated and users be notified without shipping a new build.
///
/// Wire format (one file, so a CDN can never serve a payload and signature from different versions):
///   { "payload": "<base64 of config JSON bytes>", "signature": "<base64 Ed25519 signature of those bytes>" }
public struct RemoteConfig: Codable, Equatable, Sendable {
    /// Monotonic version; a config is only accepted if its serial is higher than the last accepted one.
    public var serial: Int
    public var apiId: Int?
    public var apiHash: String?
    /// api_hash run through `Obfuscator`, so the public config file does not contain it in plain text.
    public var apiHashObfuscated: String?
    /// Versions below this get an "update required" banner.
    public var minAppVersion: String?
    /// Optional announcement shown to every user.
    public var message: String?

    enum CodingKeys: String, CodingKey {
        case serial
        case apiId = "api_id"
        case apiHash = "api_hash"
        case apiHashObfuscated = "api_hash_obf"
        case minAppVersion = "min_app_version"
        case message
    }

    public init(serial: Int, apiId: Int? = nil, apiHash: String? = nil, apiHashObfuscated: String? = nil, minAppVersion: String? = nil, message: String? = nil) {
        self.serial = serial
        self.apiId = apiId
        self.apiHash = apiHash
        self.apiHashObfuscated = apiHashObfuscated
        self.minAppVersion = minAppVersion
        self.message = message
    }

    public var credentials: ApiCredentials? {
        guard let apiId, let apiHash = apiHash ?? apiHashObfuscated.flatMap(Obfuscator.decode) else { return nil }
        let credentials = ApiCredentials(apiId: apiId, apiHash: apiHash)
        return credentials.isValid ? credentials : nil
    }

    public func requiresUpdate(currentVersion: String) -> Bool {
        guard let minAppVersion else { return false }
        return AppVersion.compare(currentVersion, minAppVersion) == .orderedAscending
    }
}

public struct SignedEnvelope: Codable, Equatable, Sendable {
    public var payload: String
    public var signature: String

    public init(payload: String, signature: String) {
        self.payload = payload
        self.signature = signature
    }
}

public enum RemoteConfigError: Error, Equatable {
    case malformed
    case badSignature
    case notNewer
}

public struct RemoteConfigVerifier: Sendable {
    private let publicKey: Curve25519.Signing.PublicKey

    /// `publicKeyBase64` is the raw 32-byte Ed25519 public key, base64-encoded.
    public init?(publicKeyBase64: String) {
        guard let raw = Data(base64Encoded: publicKeyBase64.trimmingCharacters(in: .whitespacesAndNewlines)),
              let key = try? Curve25519.Signing.PublicKey(rawRepresentation: raw) else { return nil }
        self.publicKey = key
    }

    public func verify(_ envelopeData: Data) throws -> RemoteConfig {
        guard let envelope = try? JSONDecoder().decode(SignedEnvelope.self, from: envelopeData),
              let payload = Data(base64Encoded: envelope.payload),
              let signature = Data(base64Encoded: envelope.signature) else { throw RemoteConfigError.malformed }
        guard publicKey.isValidSignature(signature, for: payload) else { throw RemoteConfigError.badSignature }
        guard let config = try? JSONDecoder().decode(RemoteConfig.self, from: payload) else { throw RemoteConfigError.malformed }
        return config
    }

    /// Used by the signing tool and tests.
    public static func sign(_ config: RemoteConfig, with privateKey: Curve25519.Signing.PrivateKey) throws -> Data {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.sortedKeys]
        let payload = try encoder.encode(config)
        let signature = try privateKey.signature(for: payload)
        let envelope = SignedEnvelope(payload: payload.base64EncodedString(), signature: signature.base64EncodedString())
        let out = JSONEncoder()
        out.outputFormatting = [.prettyPrinted, .sortedKeys]
        return try out.encode(envelope)
    }
}

/// Fetches, verifies and caches the remote config. URLs are tried in order (e.g. GitHub Pages first,
/// own server as fallback); the next one is tried on network errors, invalid signatures, or a stale copy.
/// The cache keeps the signed envelope and is re-verified on load, so local tampering is detected too.
public actor RemoteConfigService {
    public typealias Fetch = @Sendable (URL) async throws -> Data

    private let urls: [URL]
    private let verifier: RemoteConfigVerifier
    private let keychain: KeychainStore
    private let fetch: Fetch
    private(set) public var current: RemoteConfig?
    /// Verified cached config at launch, readable synchronously so the app can start with the right key.
    public nonisolated let initial: RemoteConfig?

    public static let keychainAccount = "remote-config"

    public init(urls: [URL], verifier: RemoteConfigVerifier, keychain: KeychainStore, fetch: @escaping Fetch = RemoteConfigService.defaultFetch) {
        self.urls = urls
        self.verifier = verifier
        self.keychain = keychain
        self.fetch = fetch
        let cached = keychain.data(for: Self.keychainAccount).flatMap { try? verifier.verify($0) }
        self.current = cached
        self.initial = cached
    }

    /// Downloads the config. Returns the new config if it was accepted, nil if unchanged or invalid.
    @discardableResult
    public func refresh() async -> RemoteConfig? {
        for url in urls {
            do {
                let data = try await fetch(url)
                return try accept(data)
            } catch RemoteConfigError.notNewer {
                continue
            } catch {
                NSLog("TeleSave: remote config from \(url.host ?? "?") unusable: \(error)")
                continue
            }
        }
        return nil
    }

    func accept(_ data: Data) throws -> RemoteConfig {
        let config = try verifier.verify(data)
        if let current, config.serial <= current.serial { throw RemoteConfigError.notNewer }
        keychain.set(data, for: Self.keychainAccount)
        current = config
        return config
    }

    public static let defaultFetch: Fetch = { url in
        var request = URLRequest(url: url, cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 15)
        request.setValue("TeleSave", forHTTPHeaderField: "User-Agent")
        let (data, response) = try await URLSession.shared.data(for: request)
        guard let http = response as? HTTPURLResponse, (200..<300).contains(http.statusCode) else {
            throw URLError(.badServerResponse)
        }
        return data
    }
}

extension RemoteConfigService {
    /// Parses a comma/whitespace separated URL list (Info.plist `TSConfigURLs`).
    public static func parseURLs(_ value: String?) -> [URL] {
        (value ?? "")
            .split(whereSeparator: { $0 == "," || $0.isWhitespace })
            .compactMap { URL(string: String($0)) }
            .filter { $0.scheme == "https" }
    }
}

public enum AppVersion {
    /// Numeric dotted-version comparison ("0.10.0" > "0.9.3").
    public static func compare(_ a: String, _ b: String) -> ComparisonResult {
        let lhs = a.split(separator: ".").map { Int($0) ?? 0 }
        let rhs = b.split(separator: ".").map { Int($0) ?? 0 }
        for i in 0..<max(lhs.count, rhs.count) {
            let l = i < lhs.count ? lhs[i] : 0
            let r = i < rhs.count ? rhs[i] : 0
            if l != r { return l < r ? .orderedAscending : .orderedDescending }
        }
        return .orderedSame
    }
}
