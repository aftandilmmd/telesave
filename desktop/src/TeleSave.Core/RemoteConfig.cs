using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace TeleSave.Core;

/// <summary>
/// Remotely managed settings (same signed format as the macOS app):
/// { "payload": base64(config JSON), "signature": base64(Ed25519 signature of those bytes) }.
/// </summary>
public sealed record RemoteConfig(
    [property: JsonPropertyName("serial")] int Serial,
    [property: JsonPropertyName("api_id")] int? ApiId = null,
    [property: JsonPropertyName("api_hash")] string? ApiHash = null,
    [property: JsonPropertyName("api_hash_obf")] string? ApiHashObfuscated = null,
    [property: JsonPropertyName("min_app_version")] string? MinAppVersion = null,
    [property: JsonPropertyName("message")] string? Message = null)
{
    public ApiCredentials? Credentials
    {
        get
        {
            var hash = ApiHash ?? (ApiHashObfuscated is { } o ? Obfuscator.Decode(o) : null);
            if (ApiId is not { } id || hash is null) return null;
            var credentials = new ApiCredentials(id, hash);
            return credentials.IsValid ? credentials : null;
        }
    }

    public bool RequiresUpdate(string currentVersion) =>
        MinAppVersion is { } min && AppVersion.Compare(currentVersion, min) < 0;
}

public enum RemoteConfigError { Malformed, BadSignature, NotNewer }

public sealed class RemoteConfigException(RemoteConfigError error) : Exception(error.ToString())
{
    public RemoteConfigError Error { get; } = error;
}

public sealed class RemoteConfigVerifier
{
    private readonly Ed25519PublicKeyParameters _key;

    private RemoteConfigVerifier(Ed25519PublicKeyParameters key) => _key = key;

    /// <summary>Raw 32-byte Ed25519 public key, base64.</summary>
    public static RemoteConfigVerifier? FromBase64(string publicKey)
    {
        try
        {
            var raw = Convert.FromBase64String(publicKey.Trim());
            return raw.Length == 32 ? new RemoteConfigVerifier(new Ed25519PublicKeyParameters(raw, 0)) : null;
        }
        catch (FormatException) { return null; }
    }

    public RemoteConfig Verify(byte[] envelopeData)
    {
        byte[] payload, signature;
        try
        {
            using var doc = JsonDocument.Parse(envelopeData);
            payload = Convert.FromBase64String(doc.RootElement.GetProperty("payload").GetString()!);
            signature = Convert.FromBase64String(doc.RootElement.GetProperty("signature").GetString()!);
        }
        catch (Exception) { throw new RemoteConfigException(RemoteConfigError.Malformed); }

        var verifier = new Ed25519Signer();
        verifier.Init(false, _key);
        verifier.BlockUpdate(payload, 0, payload.Length);
        if (!verifier.VerifySignature(signature)) throw new RemoteConfigException(RemoteConfigError.BadSignature);
        try
        {
            return JsonSerializer.Deserialize<RemoteConfig>(payload) ?? throw new RemoteConfigException(RemoteConfigError.Malformed);
        }
        catch (JsonException) { throw new RemoteConfigException(RemoteConfigError.Malformed); }
    }

    /// <summary>For tests and tooling.</summary>
    public static byte[] Sign(byte[] payload, Ed25519PrivateKeyParameters key)
    {
        var signer = new Ed25519Signer();
        signer.Init(true, key);
        signer.BlockUpdate(payload, 0, payload.Length);
        var envelope = new { payload = Convert.ToBase64String(payload), signature = Convert.ToBase64String(signer.GenerateSignature()) };
        return JsonSerializer.SerializeToUtf8Bytes(envelope);
    }
}

/// <summary>
/// Fetches, verifies and caches the remote config. URLs are tried in order (GitHub Pages first, own server
/// as fallback); the next one is tried on network errors, bad signatures, or a stale copy.
/// </summary>
public sealed class RemoteConfigService
{
    public const string SecretAccount = "remote-config";
    public delegate Task<byte[]> Fetch(Uri url);

    private readonly IReadOnlyList<Uri> _urls;
    private readonly RemoteConfigVerifier _verifier;
    private readonly ISecretStore _store;
    private readonly Fetch _fetch;
    private readonly object _gate = new();

    public RemoteConfig? Current { get; private set; }
    /// <summary>Verified cached config at launch.</summary>
    public RemoteConfig? Initial { get; }

    public RemoteConfigService(IReadOnlyList<Uri> urls, RemoteConfigVerifier verifier, ISecretStore store, Fetch? fetch = null)
    {
        _urls = urls;
        _verifier = verifier;
        _store = store;
        _fetch = fetch ?? DefaultFetch;
        if (store.Read(SecretAccount) is { } cached)
        {
            try { Current = Initial = verifier.Verify(cached); } catch (RemoteConfigException) { }
        }
    }

    /// <summary>The new config if one was accepted; null if unchanged, invalid or unreachable.</summary>
    public async Task<RemoteConfig?> RefreshAsync()
    {
        foreach (var url in _urls)
        {
            try
            {
                var data = await _fetch(url).ConfigureAwait(false);
                return Accept(data);
            }
            catch (Exception)
            {
                // Next source: unreachable, forged, or not newer.
            }
        }
        return null;
    }

    internal RemoteConfig Accept(byte[] data)
    {
        var config = _verifier.Verify(data);
        lock (_gate)
        {
            if (Current is { } current && config.Serial <= current.Serial) throw new RemoteConfigException(RemoteConfigError.NotNewer);
            _store.Write(SecretAccount, data);
            Current = config;
        }
        return config;
    }

    public static IReadOnlyList<Uri> ParseUrls(string? value) =>
        (value ?? "").Split([',', ' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(s => Uri.TryCreate(s, UriKind.Absolute, out var u) ? u : null)
            .Where(u => u is not null && u.Scheme == Uri.UriSchemeHttps).Select(u => u!).ToList();

    private static readonly HttpClient Http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TeleSave");
        return client;
    }

    private static Task<byte[]> DefaultFetch(Uri url) => Http.GetByteArrayAsync(url);
}

public static class AppVersion
{
    /// <summary>Numeric dotted-version comparison ("0.10.0" &gt; "0.9.3").</summary>
    public static int Compare(string a, string b)
    {
        static int[] Parts(string v) => v.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        var l = Parts(a);
        var r = Parts(b);
        for (var i = 0; i < Math.Max(l.Length, r.Length); i++)
        {
            var x = i < l.Length ? l[i] : 0;
            var y = i < r.Length ? r[i] : 0;
            if (x != y) return x < y ? -1 : 1;
        }
        return 0;
    }
}

/// <summary>
/// Light obfuscation for the bundled / published api_hash so it is not a plain string.
/// Not encryption. Must match TeleSaveCore.Obfuscator in the macOS app.
/// </summary>
public static class Obfuscator
{
    private static readonly byte[] Key = Encoding.UTF8.GetBytes("TeleSave.obfuscation.v1");

    public static string Encode(string plain) => Convert.ToBase64String(Xor(Encoding.UTF8.GetBytes(plain)));

    public static string? Decode(string encoded)
    {
        try { return Encoding.UTF8.GetString(Xor(Convert.FromBase64String(encoded))); }
        catch (FormatException) { return null; }
    }

    private static byte[] Xor(byte[] bytes) => bytes.Select((b, i) => (byte)(b ^ Key[i % Key.Length])).ToArray();
}

public static class CredentialsResolver
{
    public const string SecretAccount = "api-credentials";

    /// <summary>
    /// Order: remote config (signed by the developer) → bundled key → user-entered key → environment.
    /// A build with a bundled key never uses a key typed in by the user.
    /// </summary>
    public static ApiCredentials? Resolve(ISecretStore store, ApiCredentials? remote, ApiCredentials? bundled, IDictionary<string, string?>? environment = null)
    {
        if (remote?.IsValid == true) return remote;
        if (bundled?.IsValid == true) return bundled;
        if (store.Read(SecretAccount) is { } data)
        {
            try
            {
                var saved = JsonSerializer.Deserialize<ApiCredentials>(data);
                if (saved?.IsValid == true) return saved;
            }
            catch (JsonException) { }
        }
        environment ??= Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
            .ToDictionary(e => (string)e.Key, e => e.Value as string);
        if (environment.TryGetValue("TELESAVE_API_ID", out var idText) && int.TryParse(idText, out var id)
            && environment.TryGetValue("TELESAVE_API_HASH", out var hash) && hash is not null)
        {
            var env = new ApiCredentials(id, hash);
            if (env.IsValid) return env;
        }
        return null;
    }

    public static bool Save(ApiCredentials credentials, ISecretStore store) =>
        store.Write(SecretAccount, JsonSerializer.SerializeToUtf8Bytes(credentials));
}
