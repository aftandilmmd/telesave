import XCTest
@testable import TeleSaveCore

/// Envelope produced by `scripts/remote-config.swift sign` — guards tool/app format compatibility.
final class RemoteConfigToolCompatibilityTests: XCTestCase {
    let publicKey = "NSiVu2vBmvn8hL6+2iRT0ey/6Mrocyp3v3oNzHRVFjE="
    let envelope = #"{"payload":"eyJhcGlfaGFzaF9vYmYiOiJNZ0FJQmpFQVQxMFpXVmRTUmtGU1VSSU1DdzFNRndoc1VscFFaMUpFVkI0PSIsImFwaV9pZCI6NzY1NDMyMSwibWVzc2FnZSI6IkJha2ltIHZhciIsIm1pbl9hcHBfdmVyc2lvbiI6IjAuMy4wIiwic2VyaWFsIjo0fQ==","signature":"fWB3k2bO7T1AE0y2YtBH21zEGLzToLIDDdKwS/koGI0BD94wjB7LeVlkxSYAiVaAYRjUUDtOC6SqOxmDVEqsDg=="}"#

    func testAppAcceptsToolOutput() throws {
        let verifier = try XCTUnwrap(RemoteConfigVerifier(publicKeyBase64: publicKey))
        let config = try verifier.verify(Data(envelope.utf8))
        XCTAssertEqual(config.serial, 4)
        XCTAssertNil(config.apiHash, "tool must not publish the plain api_hash")
        XCTAssertEqual(config.credentials, ApiCredentials(apiId: 7654321, apiHash: "fedcba9876543210fedcba9876543210"))
        XCTAssertEqual(config.message, "Bakim var")
        XCTAssertTrue(config.requiresUpdate(currentVersion: "0.1.0"))
    }
}
