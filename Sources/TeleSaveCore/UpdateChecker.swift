import Foundation

/// A downloadable file of a release.
public struct ReleaseAsset: Equatable, Sendable {
    public let name: String
    /// nil when the API response has no download URL for it.
    public let url: URL?
    public let size: Int64
    /// Lowercase hex SHA-256 from GitHub's "digest" field ("sha256:…"), when GitHub provides one.
    public let sha256: String?
}

/// Latest published release on GitHub.
public struct ReleaseInfo: Equatable, Sendable {
    /// Version without a leading "v" ("0.2.0").
    public let version: String
    public let pageURL: URL
    public let assets: [ReleaseAsset]

    /// The macOS disk image.
    public var dmg: ReleaseAsset? { assets.first { $0.name.lowercased().hasSuffix(".dmg") } }

    /// Parses the GitHub REST `releases/latest` response. Drafts, pre-releases and releases without a macOS
    /// download (.dmg) are ignored, so a Windows-only release never prompts macOS users.
    public static func parse(_ data: Data) -> ReleaseInfo? {
        guard let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let tag = json["tag_name"] as? String,
              let page = (json["html_url"] as? String).flatMap(URL.init(string:)),
              json["draft"] as? Bool != true, json["prerelease"] as? Bool != true else { return nil }
        let assets = (json["assets"] as? [[String: Any]] ?? []).compactMap(parseAsset)
        guard assets.contains(where: { $0.name.lowercased().hasSuffix(".dmg") }) else { return nil }
        let version = tag.hasPrefix("v") || tag.hasPrefix("V") ? String(tag.dropFirst()) : tag
        guard version.first?.isNumber == true else { return nil }
        return ReleaseInfo(version: version, pageURL: page, assets: assets)
    }

    private static func parseAsset(_ json: [String: Any]) -> ReleaseAsset? {
        guard let name = json["name"] as? String else { return nil }
        let url = (json["browser_download_url"] as? String).flatMap(URL.init(string:))
        let digest = (json["digest"] as? String).flatMap { value -> String? in
            guard value.lowercased().hasPrefix("sha256:") else { return nil }
            return String(value.dropFirst(7)).lowercased()
        }
        return ReleaseAsset(name: name, url: url, size: (json["size"] as? NSNumber)?.int64Value ?? 0, sha256: digest)
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
