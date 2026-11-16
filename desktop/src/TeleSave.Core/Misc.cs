using System.Text.Json;
using System.Text.Json.Nodes;

namespace TeleSave.Core;

/// <summary>Latest published release on GitHub that has a download for this platform.</summary>
public sealed record ReleaseInfo(string Version, Uri PageUrl)
{
    /// <summary>
    /// Parses the GitHub REST releases/latest response. Drafts, pre-releases and releases without an
    /// asset for this platform are ignored, so a Windows-only fix never prompts macOS users and vice versa.
    /// </summary>
    public static ReleaseInfo? Parse(string json, Func<string, bool> isPlatformAsset)
    {
        try
        {
            var root = JsonNode.Parse(json) as JsonObject;
            if (root is null || root["draft"]?.GetValue<bool>() == true || root["prerelease"]?.GetValue<bool>() == true) return null;
            var tag = root["tag_name"]?.GetValue<string>();
            var page = root["html_url"]?.GetValue<string>();
            if (tag is null || page is null) return null;
            var assets = (root["assets"] as JsonArray)?.OfType<JsonObject>().Select(a => a["name"]?.GetValue<string>() ?? "") ?? [];
            if (!assets.Any(isPlatformAsset)) return null;
            var version = tag.TrimStart('v', 'V');
            if (version.Length == 0 || !char.IsDigit(version[0])) return null;
            return new ReleaseInfo(version, new Uri(page));
        }
        catch (JsonException) { return null; }
    }

    public bool IsNewerThan(string current) => AppVersion.Compare(Version, current) > 0;
}

public static class UpdateChecker
{
    public const string Repository = "aftandilmmd/telesave";
    public static readonly Uri RepositoryUrl = new($"https://github.com/{Repository}");

    /// <summary>Windows builds are published as "TeleSave-x.y.z-windows-…".</summary>
    public static bool IsWindowsAsset(string name) => name.Contains("-windows-", StringComparison.OrdinalIgnoreCase);

    public delegate Task<(int Status, string Body)> Fetch(Uri url);

    public static async Task<ReleaseInfo?> LatestReleaseAsync(Func<string, bool> isPlatformAsset, Fetch? fetch = null)
    {
        var url = new Uri($"https://api.github.com/repos/{Repository}/releases/latest");
        try
        {
            var (status, body) = await (fetch ?? DefaultFetch)(url).ConfigureAwait(false);
            return status == 200 ? ReleaseInfo.Parse(body, isPlatformAsset) : null;
        }
        catch (Exception) { return null; }
    }

    private static async Task<(int, string)> DefaultFetch(Uri url)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("TeleSave");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.GetAsync(url).ConfigureAwait(false);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync().ConfigureAwait(false));
    }
}

/// <summary>Messages TeleSave sent from this computer, so they can be marked in a chat's file list.</summary>
public sealed class SentMessageRegistry
{
    private readonly HashSet<string> _keys;

    public SentMessageRegistry(IEnumerable<string>? keys = null) => _keys = new HashSet<string>(keys ?? []);

    private static string Key(long owner, long? chatId, long messageId) => $"{owner}|{(chatId?.ToString() ?? "saved")}|{messageId}";

    public bool Contains(long owner, long? chatId, long messageId) { lock (_keys) return _keys.Contains(Key(owner, chatId, messageId)); }
    public bool Insert(long owner, long? chatId, long messageId) { lock (_keys) return _keys.Add(Key(owner, chatId, messageId)); }
    public bool Remove(long owner, long? chatId, long messageId) { lock (_keys) return _keys.Remove(Key(owner, chatId, messageId)); }
    public int Count { get { lock (_keys) return _keys.Count; } }

    public static SentMessageRegistry Load(string path)
    {
        try { return new SentMessageRegistry(JsonSerializer.Deserialize<string[]>(File.ReadAllText(path))); }
        catch (Exception) { return new SentMessageRegistry(); }
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string[] snapshot;
            lock (_keys) snapshot = _keys.ToArray();
            File.WriteAllText(path, JsonSerializer.Serialize(snapshot));
        }
        catch (Exception) { }
    }
}

/// <summary>Decides from TDLib's ChatMemberStatus whether the user may send files to a group or channel.</summary>
public static class ChatPermissionRules
{
    public static bool CanSendFiles(JsonObject? status, bool isChannel, bool defaultPermission) => status?["@type"]?.GetValue<string>() switch
    {
        "chatMemberStatusCreator" => true,
        "chatMemberStatusAdministrator" => !isChannel || ((status["rights"] as JsonObject)?["can_post_messages"]?.GetValue<bool>() ?? false),
        "chatMemberStatusMember" => !isChannel && defaultPermission,
        "chatMemberStatusRestricted" => !isChannel && ((status["permissions"] as JsonObject)?["can_send_documents"]?.GetValue<bool>() ?? false),
        _ => false
    };
}

public static class MessageFileLocator
{
    /// <summary>A file name in the folder that does not exist yet: "a.pdf", "a (2).pdf", …</summary>
    public static string UniquePath(string directory, string fileName)
    {
        var candidate = Path.Combine(directory, fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var n = 2; File.Exists(candidate); n++) candidate = Path.Combine(directory, $"{stem} ({n}){ext}");
        return candidate;
    }
}

/// <summary>Smoothed upload speed and ETA from periodic snapshots.</summary>
public sealed class TransferRate(double smoothing = 0.3)
{
    private double? _lastBytes;
    private DateTimeOffset? _lastTime;

    public double BytesPerSecond { get; private set; }

    public void Sample(double uploadedBytes, DateTimeOffset time)
    {
        if (_lastBytes is not { } previous || _lastTime is not { } previousTime)
        {
            _lastBytes = uploadedBytes;
            _lastTime = time;
            return;
        }
        var dt = (time - previousTime).TotalSeconds;
        if (dt < 0.2) return;
        _lastBytes = uploadedBytes;
        _lastTime = time;
        var delta = uploadedBytes - previous;
        // A job finishing drops its bytes from the in-flight sum; ignore that negative step.
        if (delta < 0) return;
        var instant = delta / dt;
        BytesPerSecond = BytesPerSecond == 0 ? instant : BytesPerSecond * (1 - smoothing) + instant * smoothing;
    }

    public void Reset() { BytesPerSecond = 0; _lastBytes = null; _lastTime = null; }

    public TimeSpan? Eta(double remainingBytes) => BytesPerSecond > 1 ? TimeSpan.FromSeconds(remainingBytes / BytesPerSecond) : null;
}
