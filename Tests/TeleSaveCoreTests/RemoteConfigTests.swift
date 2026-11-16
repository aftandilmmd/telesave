import CryptoKit
import XCTest
@testable import TeleSaveCore

final class RemoteConfigTests: XCTestCase {
    let privateKey = Curve25519.Signing.PrivateKey()
    var keychain: KeychainStore!
    let primary = URL(string: "https://example.github.io/telesave/config.json")!
    let fallback = URL(string: "https://example.com/telesave/config.json")!
    let apiHashValue = "0123456789abcdef0123456789abcdef"

    override func setUp() {
        keychain = KeychainStore(service: "com.telesave.tests.\(UUID().uuidString)")
    }

    override func tearDown() {
        keychain.delete(RemoteConfigService.keychainAccount)
    }

    private var verifier: RemoteConfigVerifier {
        RemoteConfigVerifier(publicKeyBase64: privateKey.publicKey.rawRepresentation.base64EncodedString())!
    }

    private func signed(_ config: RemoteConfig, key: Curve25519.Signing.PrivateKey? = nil) -> Data {
        try! RemoteConfigVerifier.sign(config, with: key ?? privateKey)
    }

    /// Fake network: per-URL responses; a missing entry throws.
    private func fetch(_ responses: [URL: Data]) -> RemoteConfigService.Fetch {
        { url in
            guard let data = responses[url] else { throw URLError(.cannotConnectToHost) }
            return data
        }
    }

    func testSignatureRoundTrip() throws {
        let config = RemoteConfig(serial: 1, apiId: 42, apiHash: apiHashValue, minAppVersion: "0.2.0", message: "Merhaba")
        XCTAssertEqual(try verifier.verify(signed(config)), config)
        XCTAssertEqual(config.credentials, ApiCredentials(apiId: 42, apiHash: apiHashValue))
    }

    func testObfuscatedHashInConfig() {
        let config = RemoteConfig(serial: 1, apiId: 42, apiHashObfuscated: Obfuscator.encode(apiHashValue))
        XCTAssertEqual(config.credentials, ApiCredentials(apiId: 42, apiHash: apiHashValue))
    }

    func testRejectsWrongKeyAndTamperedPayload() throws {
        let config = RemoteConfig(serial: 1, apiId: 42, apiHash: apiHashValue)
        XCTAssertThrowsError(try verifier.verify(signed(config, key: .init()))) {
            XCTAssertEqual($0 as? RemoteConfigError, .badSignature)
        }

        var envelope = try JSONDecoder().decode(SignedEnvelope.self, from: signed(config))
        var payload = try JSONDecoder().decode(RemoteConfig.self, from: Data(base64Encoded: envelope.payload)!)
        payload.apiId = 666
        envelope.payload = try JSONEncoder().encode(payload).base64EncodedString()
        XCTAssertThrowsError(try verifier.verify(JSONEncoder().encode(envelope))) {
            XCTAssertEqual($0 as? RemoteConfigError, .badSignature)
        }
        XCTAssertThrowsError(try verifier.verify(Data("not json".utf8))) {
            XCTAssertEqual($0 as? RemoteConfigError, .malformed)
        }
    }

    func testInvalidPublicKey() {
        XCTAssertNil(RemoteConfigVerifier(publicKeyBase64: "abc"))
    }

    func testPrimaryUsedWhenAvailable() async {
        let service = RemoteConfigService(urls: [primary, fallback], verifier: verifier, keychain: keychain, fetch: fetch([
            primary: signed(RemoteConfig(serial: 1, apiId: 1, apiHash: apiHashValue)),
            fallback: signed(RemoteConfig(serial: 1, apiId: 2, apiHash: apiHashValue))
        ]))
        let accepted = await service.refresh()
        XCTAssertEqual(accepted?.apiId, 1)
    }

    func testFallsBackWhenPrimaryFailsOrIsInvalid() async {
        // Primary down.
        let down = RemoteConfigService(urls: [primary, fallback], verifier: verifier, keychain: keychain, fetch: fetch([
            fallback: signed(RemoteConfig(serial: 1, apiId: 2, apiHash: apiHashValue))
        ]))
        let fromFallback = await down.refresh()
        XCTAssertEqual(fromFallback?.apiId, 2)

        // Primary serves a forged config.
        keychain.delete(RemoteConfigService.keychainAccount)
        let forged = RemoteConfigService(urls: [primary, fallback], verifier: verifier, keychain: keychain, fetch: fetch([
            primary: signed(RemoteConfig(serial: 9, apiId: 666, apiHash: apiHashValue), key: .init()),
            fallback: signed(RemoteConfig(serial: 1, apiId: 2, apiHash: apiHashValue))
        ]))
        let notForged = await forged.refresh()
        XCTAssertEqual(notForged?.apiId, 2)
    }

    func testFallbackUsedWhenPrimaryIsStale() async {
        let seeded = RemoteConfigService(urls: [primary], verifier: verifier, keychain: keychain, fetch: fetch([
            primary: signed(RemoteConfig(serial: 5, apiId: 1, apiHash: apiHashValue))
        ]))
        await seeded.refresh()

        let service = RemoteConfigService(urls: [primary, fallback], verifier: verifier, keychain: keychain, fetch: fetch([
            primary: signed(RemoteConfig(serial: 5, apiId: 1, apiHash: apiHashValue)),
            fallback: signed(RemoteConfig(serial: 6, apiId: 3, apiHash: apiHashValue))
        ]))
        let newer = await service.refresh()
        XCTAssertEqual(newer?.serial, 6)
    }

    func testRejectsRollbackAndKeepsCache() async {
        let service = RemoteConfigService(urls: [primary], verifier: verifier, keychain: keychain, fetch: fetch([
            primary: signed(RemoteConfig(serial: 3, apiId: 7, apiHash: apiHashValue))
        ]))
        await service.refresh()

        let replay = RemoteConfigService(urls: [primary], verifier: verifier, keychain: keychain, fetch: fetch([
            primary: signed(RemoteConfig(serial: 2, apiId: 8, apiHash: apiHashValue))
        ]))
        let cachedSerial = await replay.current?.serial
        XCTAssertEqual(cachedSerial, 3, "cache survives restart and is re-verified")
        let accepted = await replay.refresh()
        XCTAssertNil(accepted, "older serial must be rejected")
        let stillSeven = await replay.current?.apiId
        XCTAssertEqual(stillSeven, 7)
    }

    func testTamperedCacheIsIgnored() async {
        keychain.set(signed(RemoteConfig(serial: 1, apiId: 1, apiHash: apiHashValue), key: .init()), for: RemoteConfigService.keychainAccount)
        let service = RemoteConfigService(urls: [primary], verifier: verifier, keychain: keychain, fetch: fetch([:]))
        let current = await service.current
        XCTAssertNil(current)
    }

    func testAllSourcesDown() async {
        let service = RemoteConfigService(urls: [primary, fallback], verifier: verifier, keychain: keychain, fetch: fetch([:]))
        let result = await service.refresh()
        XCTAssertNil(result)
    }

    func testURLParsingKeepsOrderAndRequiresHTTPS() {
        let urls = RemoteConfigService.parseURLs("https://a.github.io/c.json, http://insecure/c.json  https://b.com/c.json")
        XCTAssertEqual(urls.map(\.host), ["a.github.io", "b.com"])
        XCTAssertEqual(RemoteConfigService.parseURLs(nil), [])
    }

    func testCredentialPrecedence() {
        let remote = ApiCredentials(apiId: 5, apiHash: apiHashValue)
        let env = ["TELESAVE_API_ID": "9", "TELESAVE_API_HASH": apiHashValue]
        XCTAssertEqual(CredentialsResolver.resolve(keychain: keychain, remote: remote, bundle: Bundle(for: Self.self), environment: env)?.apiId, 5)
        XCTAssertEqual(CredentialsResolver.resolve(keychain: keychain, remote: nil, bundle: Bundle(for: Self.self), environment: env)?.apiId, 9)
    }

    func testVersionComparison() {
        XCTAssertEqual(AppVersion.compare("0.9.3", "0.10.0"), .orderedAscending)
        XCTAssertEqual(AppVersion.compare("1.0", "1.0.0"), .orderedSame)
        XCTAssertEqual(AppVersion.compare("2.0.1", "2.0.0"), .orderedDescending)
        XCTAssertTrue(RemoteConfig(serial: 1, minAppVersion: "0.2.0").requiresUpdate(currentVersion: "0.1.0"))
        XCTAssertFalse(RemoteConfig(serial: 1, minAppVersion: "0.2.0").requiresUpdate(currentVersion: "0.2.0"))
        XCTAssertFalse(RemoteConfig(serial: 1).requiresUpdate(currentVersion: "0.1.0"))
    }
}

final class TransportSwapTests: XCTestCase {
    func testReplaceTransportRequeuesAndIgnoresOldResults() async {
        let dir = makeTempDirectory()
        defer { try? FileManager.default.removeItem(at: dir) }
        let old = FakeTransport()
        let queue = UploadQueue(transport: old)
        await queue.setPaused(false)
        await queue.enqueue([prepared(makeFile(in: dir, name: "a.txt", bytes: 10))])
        _ = await eventually { await queue.items[0].messageId != nil }
        let oldMessage = await queue.items[0].messageId!

        let new = FakeTransport(firstMessageId: 5001)
        await queue.replaceTransport(new)
        let resent = await eventually { new.sendCount == 1 }
        XCTAssertTrue(resent, "in-flight item restarts on the new client")

        // A late success from the old client must not complete the item.
        await queue.handle(.sendSucceeded(oldMessageId: oldMessage, newMessageId: 1))
        let state = await queue.items[0].state
        guard case .uploading = state else { return XCTFail("expected uploading, got \(state)") }

        let newMessage = await queue.items[0].messageId!
        await queue.handle(.sendSucceeded(oldMessageId: newMessage, newMessageId: 2))
        let done = await queue.items[0].state
        XCTAssertEqual(done, .done(messageId: 2))
    }
}
