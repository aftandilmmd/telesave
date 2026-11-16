import Foundation

/// Latest published release on GitHub.
public struct ReleaseInfo: Equatable, Sendable {
    /// Version without a leading "v" ("0.2.0").
    public let version: String
    public let pageURL: URL

    /// Parses the GitHub REST `releases/latest` response; drafts and pre-releases are ignored.
    public static func parse(_ data: Data) -> ReleaseInfo? {
        guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let tag = json["tag_name"] as? String,
              let page = (json["html_url"] as? String).flatMap(URL.init(string:)),
              json["draft"] as? Bool != true, json["prerelease"] as? Bool != true else { return nil }
        let version = tag.hasPrefix("v") || tag.hasPrefix("V") ? String(tag.dropFirst()) : tag
        guard version.first?.isNumber == true else { return nil }
        return ReleaseInfo(version: version, pageURL: page)
    }

    public func isNewer(than current: String) -> Bool {
        AppVersion.compare(version, current) == .orderedDescending
    }
}

/// Checks GitHub for a newer release.
public enum UpdateChecker {
    public static let repository = "aftandilmmd/telesave"
    public static let repositoryURL = URL(string: "https://github.com/\(repository)")!

    public typealias Fetch = @Sendable (URLRequest) async throws -> (Data, URLResponse)

    /// The latest release, or nil when there is none yet, GitHub is unreachable, or rate-limited.
    public static func latestRelease(fetch: Fetch = { try await URLSession.shared.data(for: $0) }) async -> ReleaseInfo? {
        var request = URLRequest(url: URL(string: "https://api.github.com/repos/\(repository)/releases/latest")!,
                                 cachePolicy: .reloadIgnoringLocalCacheData, timeoutInterval: 15)
        request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        request.setValue("TeleSave", forHTTPHeaderField: "User-Agent")
        guard let (data, response) = try? await fetch(request),
              (response as? HTTPURLResponse)?.statusCode == 200 else { return nil }
        return ReleaseInfo.parse(data)
    }
}
