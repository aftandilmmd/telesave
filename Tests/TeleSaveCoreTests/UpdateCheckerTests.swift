import XCTest
@testable import TeleSaveCore

final class UpdateCheckerTests: XCTestCase {
    private func json(_ object: [String: Any]) -> Data { try! JSONSerialization.data(withJSONObject: object) }

    func testParsesReleaseAndStripsV() throws {
        let release = try XCTUnwrap(ReleaseInfo.parse(json([
            "tag_name": "v0.2.0", "html_url": "https://github.com/aftandilmmd/telesave/releases/tag/v0.2.0",
            "draft": false, "prerelease": false, "assets": [["name": "TeleSave-0.2.0.dmg"]]
        ])))
        XCTAssertEqual(release.version, "0.2.0")
        XCTAssertTrue(release.isNewer(than: "0.1.0"))
        XCTAssertFalse(release.isNewer(than: "0.2.0"))
        XCTAssertFalse(release.isNewer(than: "0.10.0"))
    }

    func testIgnoresReleasesWithoutMacDownload() {
        XCTAssertNil(ReleaseInfo.parse(json([
            "tag_name": "v0.3.1", "html_url": "https://x.y",
            "assets": [["name": "TeleSave-0.3.1-windows-x64-setup.exe"], ["name": "TeleSave-0.3.1-windows-x64.zip"]]
        ])), "a Windows-only fix must not prompt macOS users")
    }

    func testIgnoresDraftsPrereleasesAndJunk() {
        XCTAssertNil(ReleaseInfo.parse(json(["tag_name": "v1.0", "html_url": "https://x.y", "prerelease": true])))
        XCTAssertNil(ReleaseInfo.parse(json(["tag_name": "v1.0", "html_url": "https://x.y", "draft": true])))
        XCTAssertNil(ReleaseInfo.parse(json(["tag_name": "nightly", "html_url": "https://x.y"])))
        XCTAssertNil(ReleaseInfo.parse(Data("{".utf8)))
    }

    func testNoReleaseYetOrOffline() async {
        let notFound: UpdateChecker.Fetch = { request in
            (Data(#"{"message":"Not Found"}"#.utf8), HTTPURLResponse(url: request.url!, statusCode: 404, httpVersion: nil, headerFields: nil)!)
        }
        let release1 = await UpdateChecker.latestRelease(fetch: notFound)
        XCTAssertNil(release1)
        let offline: UpdateChecker.Fetch = { _ in throw URLError(.notConnectedToInternet) }
        let release2 = await UpdateChecker.latestRelease(fetch: offline)
        XCTAssertNil(release2)
    }

    func testRequestTargetsRepository() async {
        let ok: UpdateChecker.Fetch = { request in
            XCTAssertEqual(request.url?.absoluteString, "https://api.github.com/repos/aftandilmmd/telesave/releases/latest")
            let body = try JSONSerialization.data(withJSONObject: [
                "tag_name": "0.3.0", "html_url": "https://github.com/aftandilmmd/telesave/releases/tag/0.3.0",
                "assets": [["name": "TeleSave-0.3.0.dmg"], ["name": "TeleSave-0.3.0-windows-x64-setup.exe"]]
            ] as [String: Any])
            return (body, HTTPURLResponse(url: request.url!, statusCode: 200, httpVersion: nil, headerFields: nil)!)
        }
        let release = await UpdateChecker.latestRelease(fetch: ok)
        XCTAssertEqual(release?.version, "0.3.0")
    }
}
