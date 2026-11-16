using System.Text;
using System.Text.Json.Nodes;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using TeleSave.Core;

namespace TeleSave.Core.Tests;

public class IntakeTests : IDisposable
{
    private readonly TempDir _dir = new();
    public void Dispose() => _dir.Dispose();
    private string Staging => Path.Combine(_dir.Path, "staging");

    [Fact]
    public void AcceptsFileRejectsEmptyMissingAndTooLarge()
    {
        var intake = new DropIntake(Staging);
        var result = intake.Prepare([_dir.File("ok.txt", 10), _dir.File("empty.txt", 0), _dir.File("big.bin", 200), Path.Combine(_dir.Path, "missing.txt")], 100);
        Assert.Equal(["ok.txt"], result.Accepted.Select(f => f.DisplayName));
        Assert.Equal([IntakeRejection.Reason.Empty, IntakeRejection.Reason.TooLarge, IntakeRejection.Reason.Unreadable], result.Rejected.Select(r => r.Why));
    }

    [Fact]
    public void ZipsFoldersAndRejectsDuplicates()
    {
        _dir.File("Photos/1.txt", 100);
        _dir.File("Photos/2.txt", 100);
        var intake = new DropIntake(Staging);
        var zip = Assert.Single(intake.Prepare([Path.Combine(_dir.Path, "Photos")], 1_000_000).Accepted);
        Assert.Equal("Photos.zip", zip.DisplayName);
        Assert.True(zip.IsTemporary);
        Assert.Equal("PK"u8.ToArray(), File.ReadAllBytes(zip.Path)[..2]);

        var file = _dir.File("a.txt", 10);
        Assert.Single(intake.Prepare([file], 100).Accepted);
        Assert.Equal(IntakeRejection.Reason.Duplicate, Assert.Single(intake.Prepare([file], 100).Rejected).Why);
    }

    [Fact]
    public void ExpandSendsFilesInsideSkippingHidden()
    {
        _dir.File("Trip/b.jpg", 10);
        _dir.File("Trip/a.jpg", 10);
        _dir.File("Trip/.DS_Store", 10);
        _dir.File("Trip/Day 2/c.mov", 10);
        var result = new DropIntake(Staging).Prepare([Path.Combine(_dir.Path, "Trip")], 1_000_000, FolderMode.Expand);
        Assert.Equal(["a.jpg", "b.jpg", "c.mov"], result.Accepted.Select(f => f.DisplayName));
    }

    [Fact]
    public void ExpandRejectsHugeFolder()
    {
        for (var i = 0; i <= DropIntake.MaxFilesPerFolder; i++) _dir.File($"Huge/{i}.txt", 1);
        var rejection = Assert.Single(new DropIntake(Staging).Prepare([Path.Combine(_dir.Path, "Huge")], 1_000_000, FolderMode.Expand).Rejected);
        Assert.Equal(IntakeRejection.Reason.TooManyFiles, rejection.Why);
        Assert.Equal(DropIntake.MaxFilesPerFolder + 1, rejection.Count);
    }
}

public class RuleTests
{
    [Fact]
    public void ErrorClassification()
    {
        Assert.Equal(new TelegramError.FailureKind.Flood(17), new TelegramError(429, "Too Many Requests: retry after 17").Kind);
        Assert.Equal(new TelegramError.FailureKind.Flood(9), new TelegramError(420, "FLOOD_WAIT_9").Kind);
        Assert.IsType<TelegramError.FailureKind.Transient>(new TelegramError(500, "Internal").Kind);
        Assert.IsType<TelegramError.FailureKind.Permanent>(new TelegramError(400, "FILE_PARTS_INVALID").Kind);
    }

    [Fact]
    public void RetryBackoff()
    {
        var policy = new RetryPolicy(MaxAttempts: 4, BaseDelaySeconds: 2, MaxDelaySeconds: 5);
        var transient = new TelegramError(500, "x");
        Assert.Equal(TimeSpan.FromSeconds(2), policy.DelayAfter(transient, 1));
        Assert.Equal(TimeSpan.FromSeconds(4), policy.DelayAfter(transient, 2));
        Assert.Equal(TimeSpan.FromSeconds(5), policy.DelayAfter(transient, 3));
        Assert.Null(policy.DelayAfter(transient, 4));
        Assert.Null(policy.DelayAfter(new TelegramError(400, "BAD"), 1));
    }

    [Fact]
    public void MediaClassifierAndAlbums()
    {
        Assert.Equal(SendKind.Document, MediaClassifier.Kind("a.jpg", 100, false));
        Assert.Equal(SendKind.Photo, MediaClassifier.Kind("a.jpg", 100, true));
        Assert.Equal(SendKind.Document, MediaClassifier.Kind("a.png", 20L * 1024 * 1024, true));
        Assert.Equal(SendKind.Video, MediaClassifier.Kind("a.mov", 100, true));
        var files = new[] { "1.jpg", "2.pdf", "3.mp4", "4.pdf" }.Select(n => new PreparedFile(n, n, 10, false)).ToList();
        var groups = AlbumGrouper.AssignGroups(files, true).GroupBy(g => g.Group).ToList();
        Assert.Equal(2, groups.Count);
        Assert.Null(AlbumGrouper.AssignGroups([new PreparedFile("x.pdf", "x.pdf", 1, false)], false)[0].Group);
    }

    [Fact]
    public void AuthStateMapping()
    {
        Assert.IsType<AuthState.WaitPhoneNumber>(AuthState.Parse(new JsonObject { ["@type"] = "authorizationStateWaitPhoneNumber" }));
        Assert.Equal(new AuthState.WaitQrConfirmation("tg://login?token=abc"),
            AuthState.Parse(new JsonObject { ["@type"] = "authorizationStateWaitOtherDeviceConfirmation", ["link"] = "tg://login?token=abc" }));
        Assert.Equal(new AuthState.WaitCode("+905551112233", AuthState.CodeDelivery.TelegramApp), AuthState.Parse(new JsonObject
        {
            ["@type"] = "authorizationStateWaitCode",
            ["code_info"] = new JsonObject { ["phone_number"] = "+905551112233", ["type"] = new JsonObject { ["@type"] = "authenticationCodeTypeTelegramMessage" } }
        }));
        Assert.Null(AuthState.Parse(new JsonObject { ["@type"] = "authorizationStateWaitTdlibParameters" }));
    }

    [Fact]
    public void ChatPermissions()
    {
        Assert.True(ChatPermissionRules.CanSendFiles(new JsonObject { ["@type"] = "chatMemberStatusCreator" }, true, false));
        Assert.False(ChatPermissionRules.CanSendFiles(new JsonObject { ["@type"] = "chatMemberStatusMember" }, true, true));
        Assert.True(ChatPermissionRules.CanSendFiles(new JsonObject { ["@type"] = "chatMemberStatusMember" }, false, true));
        Assert.False(ChatPermissionRules.CanSendFiles(new JsonObject { ["@type"] = "chatMemberStatusMember" }, false, false));
        Assert.False(ChatPermissionRules.CanSendFiles(new JsonObject
        {
            ["@type"] = "chatMemberStatusAdministrator", ["rights"] = new JsonObject { ["can_post_messages"] = false }
        }, true, true));
    }

    [Fact]
    public void ObfuscatorMatchesMacOS()
    {
        // Value produced by the macOS build script for this hash.
        const string hash = "0123456789abcdef0123456789abcdef";
        Assert.Equal(hash, Obfuscator.Decode(Obfuscator.Encode(hash)));
        // api_hash_obf of the example config, as produced by scripts/remote-config.swift on macOS.
        Assert.Equal("ZFReVmdUQFIWVgMEFhcGB0RYXV0aQwdjXVUEMQISAEg=", Obfuscator.Encode(hash));
    }

    [Fact]
    public void UniquePath()
    {
        using var dir = new TempDir();
        Assert.EndsWith("a.pdf", MessageFileLocator.UniquePath(dir.Path, "a.pdf"));
        dir.File("a.pdf", 1);
        dir.File("a (2).pdf", 1);
        Assert.EndsWith("a (3).pdf", MessageFileLocator.UniquePath(dir.Path, "a.pdf"));
    }
}

public class SearchAndFilesTests
{
    [Fact]
    public void TurkishAndAzerbaijaniFolding()
    {
        Assert.True(SearchText.Matches("Iğdır Raporu.pdf", "igdir"));
        Assert.True(SearchText.Matches("igdir.pdf", "IĞDIR"));
        Assert.True(SearchText.Matches("ŞEMA_çizim.PNG", "sema cizim"));
        Assert.True(SearchText.Matches("Əli müqavilə.docx", "eli muqavile"));
        Assert.True(SearchText.Matches("Öğrenci Listesi.xlsx", "liste ogrenci"));
        Assert.True(SearchText.Matches("İstanbul.jpg", "istanbul"));
        Assert.False(SearchText.Matches("rapor.pdf", "rapor fatura"));
    }

    private static ChatFile F(long id, string name, ChatFileKind kind = ChatFileKind.Document, string caption = "") =>
        new(id, (int)id, kind, name, 1, DateTimeOffset.UnixEpoch, caption, null, null, true);

    private sealed class Server(Dictionary<string, List<ChatFile>> data)
    {
        public int Requests;
        public Task<(IReadOnlyList<ChatFile>, long)> Fetch(string filter, long from, int limit)
        {
            Requests++;
            var all = data.GetValueOrDefault(filter) ?? [];
            var start = from == 0 ? 0 : all.FindIndex(f => f.MessageId < from) is var i && i >= 0 ? i : all.Count;
            var page = all.Skip(start).Take(limit).ToList();
            long next = start + limit < all.Count ? page[^1].MessageId : 0;
            return Task.FromResult(((IReadOnlyList<ChatFile>)page, next));
        }
    }

    private static async Task<List<long>> CollectAll(ChatFileFilter filter, Server server, int limit, string query = "")
    {
        var ids = new List<long>();
        ChatFileCursor? cursor = null;
        do
        {
            var page = await ChatFileSearch.PageAsync(filter, query, cursor, limit, server.Fetch);
            ids.AddRange(page.Files.Select(f => f.MessageId));
            cursor = page.Cursor;
        } while (cursor is not null);
        return ids;
    }

    [Fact]
    public async Task ImagesMergePhotosAndImageDocumentsAcrossPages()
    {
        var server = new Server(new()
        {
            ["searchMessagesFilterPhoto"] = [F(90, "p", ChatFileKind.Photo), F(60, "p", ChatFileKind.Photo), F(20, "p", ChatFileKind.Photo)],
            ["searchMessagesFilterDocument"] = [F(100, "a.jpg"), F(80, "report.pdf"), F(70, "b.png"), F(50, "c.heic"), F(40, "notes.txt"), F(10, "d.jpeg")]
        });
        Assert.Equal([100, 90, 70, 60, 50, 20, 10], await CollectAll(ChatFileFilter.Images, server, 2));
    }

    [Fact]
    public async Task DocumentsExcludeImagesAndVideos()
    {
        var server = new Server(new() { ["searchMessagesFilterDocument"] = [F(3, "a.pdf"), F(2, "b.mp4"), F(1, "c.png")] });
        Assert.Equal([3], await CollectAll(ChatFileFilter.Documents, server, 10));
    }

    [Fact]
    public async Task SparseStreamDoesNotLoopForever()
    {
        var docs = Enumerable.Range(1, 200).Reverse().Select(i => F(i, $"f{i}.pdf")).ToList();
        var server = new Server(new() { ["searchMessagesFilterDocument"] = docs });
        var page = await ChatFileSearch.PageAsync(ChatFileFilter.Images, "", null, 10, server.Fetch);
        Assert.Empty(page.Files);
        Assert.True(server.Requests <= ChatFileSearch.MaxRequestsPerPage);
        Assert.Empty(await CollectAll(ChatFileFilter.Images, server, 10));
    }

    [Fact]
    public async Task QueryMatchesNameAndCaptionLocally()
    {
        var server = new Server(new()
        {
            ["searchMessagesFilterEmpty"] = [F(4, "Kira Sözleşmesi.pdf"), F(3, "notes.txt", caption: "sozlesme taslagi"), F(2, "foto.jpg"), F(1, "SOZLESME-eski.pdf")]
        });
        Assert.Equal([4, 3, 1], await CollectAll(ChatFileFilter.All, server, 10, "sözleşme"));
    }

    [Fact]
    public void ParsesDocumentAndPhoto()
    {
        var doc = ChatFileParser.Parse(JsonNode.Parse("""
            {"id":1048576,"date":1788000000,"is_outgoing":true,"content":{"@type":"messageDocument","caption":{"text":"rapor"},
             "document":{"file_name":"rapor.pdf","mime_type":"application/pdf","minithumbnail":{"data":"/9gB"},"thumbnail":{"file":{"id":7}},
             "document":{"id":9,"size":2048}}}}
            """)!.AsObject())!;
        Assert.Equal(9, doc.FileId);
        Assert.Equal("rapor.pdf", doc.FileName);
        Assert.Equal(2048, doc.Size);
        Assert.Equal(7, doc.ThumbnailFileId);
        Assert.Equal(new byte[] { 0xFF, 0xD8, 0x01 }, doc.Minithumbnail);

        var photo = ChatFileParser.Parse(JsonNode.Parse("""
            {"id":2,"date":0,"content":{"@type":"messagePhoto","photo":{"sizes":[
              {"width":1280,"height":960,"photo":{"id":30,"size":300000}},
              {"width":90,"height":67,"photo":{"id":10,"size":1000}},
              {"width":320,"height":240,"photo":{"id":20,"size":20000}}]}}}
            """)!.AsObject())!;
        Assert.Equal(30, photo.FileId);
        Assert.Equal(20, photo.ThumbnailFileId);
        Assert.True(photo.IsImage);
        Assert.Null(ChatFileParser.Parse(JsonNode.Parse("""{"id":5,"date":0,"content":{"@type":"messageText"}}""")!.AsObject()));
    }

    [Fact]
    public void SentRegistryScopedByAccountAndChat()
    {
        var registry = new SentMessageRegistry();
        Assert.True(registry.Insert(1, null, 100));
        Assert.False(registry.Insert(1, null, 100));
        Assert.False(registry.Contains(2, null, 100));
        Assert.False(registry.Contains(1, -500, 100));
    }
}

public class L10nTests
{
    public L10nTests() => L10n.SelectedCode = L10n.SystemCode;

    [Fact]
    public void SharedLanguageFilesLoad()
    {
        Assert.Equal(["az", "tr", "en", "ru"], L10n.Languages.Select(l => l.Code));
        foreach (var (code, strings) in L10n.AllStrings)
        {
            Assert.Equal(L10n.AllStrings["tr"].Keys.ToHashSet(), strings.Keys.ToHashSet());
            foreach (var (key, value) in strings) Assert.True(L10n.PlaceholderCount(value) == L10n.PlaceholderCount(key), $"{code}: {key}");
        }
    }

    /// <summary>Every L10n.T("…") literal in the Windows sources must exist in the shared language files.</summary>
    [Fact]
    public void AllUsedKeysAreTranslated()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src"));
        var pattern = new System.Text.RegularExpressions.Regex(@"(?:L10n\.T|\bT)\(""((?:[^""\\]|\\.)*)""");
        var keys = L10n.AllStrings["en"].Keys.ToHashSet();
        var missing = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".cs") || f.EndsWith(".axaml"))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .SelectMany(f => pattern.Matches(File.ReadAllText(f)).Select(m => (File: Path.GetFileName(f), Key: System.Text.RegularExpressions.Regex.Unescape(m.Groups[1].Value))))
            .Where(x => !keys.Contains(x.Key)).Select(x => $"{x.File}: {x.Key}").ToList();
        Assert.Empty(missing);
    }

    [Fact]
    public void ResolutionAndFallback()
    {
        var available = new HashSet<string> { "az", "tr", "en", "ru" };
        Assert.Equal("ru", L10n.Resolve("system", available, ["de-DE", "ru-RU"]));
        Assert.Equal("az", L10n.Resolve("system", available, ["az-Latn-AZ"]));
        Assert.Equal("en", L10n.Resolve("system", available, ["de-DE"]));
        Assert.Equal("tr", L10n.Resolve("tr", available, ["en-US"]));
        Assert.Equal("b ← a", L10n.Format("%2$@ ← %1$@", ["a", "b"]));
    }
}

public class RemoteConfigTests
{
    private readonly Ed25519PrivateKeyParameters _key = new(new SecureRandom());
    private RemoteConfigVerifier Verifier => RemoteConfigVerifier.FromBase64(Convert.ToBase64String(_key.GeneratePublicKey().GetEncoded()))!;
    private const string Hash = "0123456789abcdef0123456789abcdef";

    private byte[] Signed(int serial, int apiId, Ed25519PrivateKeyParameters? key = null) =>
        RemoteConfigVerifier.Sign(Encoding.UTF8.GetBytes($$"""{"serial":{{serial}},"api_id":{{apiId}},"api_hash_obf":"{{Obfuscator.Encode(Hash)}}","min_app_version":"0.2.0"}"""), key ?? _key);

    [Fact]
    public void VerifiesAndRejectsForgery()
    {
        var config = Verifier.Verify(Signed(1, 42));
        Assert.Equal(new ApiCredentials(42, Hash), config.Credentials);
        Assert.True(config.RequiresUpdate("0.1.0"));
        Assert.Equal(RemoteConfigError.BadSignature, Assert.Throws<RemoteConfigException>(() => Verifier.Verify(Signed(1, 42, new Ed25519PrivateKeyParameters(new SecureRandom())))).Error);
        Assert.Equal(RemoteConfigError.Malformed, Assert.Throws<RemoteConfigException>(() => Verifier.Verify("{"u8.ToArray())).Error);
    }

    [Fact]
    public async Task FallbackOrderAndRollbackProtection()
    {
        var primary = new Uri("https://example.github.io/c.json");
        var fallback = new Uri("https://example.com/c.json");
        var store = new InMemorySecretStore();
        var down = new RemoteConfigService([primary, fallback], Verifier, store, url =>
            url == fallback ? Task.FromResult(Signed(3, 2)) : throw new HttpRequestException());
        Assert.Equal(2, (await down.RefreshAsync())!.ApiId);

        var replay = new RemoteConfigService([primary], Verifier, store, _ => Task.FromResult(Signed(2, 8)));
        Assert.Equal(3, replay.Initial!.Serial);
        Assert.Null(await replay.RefreshAsync());
    }

    [Fact]
    public void MacOSToolEnvelopeIsAccepted()
    {
        // Produced by scripts/remote-config.swift (see the macOS compatibility test).
        var verifier = RemoteConfigVerifier.FromBase64(MacFixture.PublicKey)!;
        var config = verifier.Verify(Encoding.UTF8.GetBytes(MacFixture.Envelope));
        Assert.Equal(4, config.Serial);
        Assert.Equal(new ApiCredentials(7654321, "fedcba9876543210fedcba9876543210"), config.Credentials);
    }

    [Fact]
    public void ReleaseNeedsAssetForThisPlatform()
    {
        const string macOnly = """{"tag_name":"v0.2.0","html_url":"https://github.com/x/y/releases/tag/v0.2.0","draft":false,"prerelease":false,"assets":[{"name":"TeleSave-0.2.0.dmg"}]}""";
        const string both = """{"tag_name":"v0.3.0","html_url":"https://github.com/x/y/releases/tag/v0.3.0","assets":[{"name":"TeleSave-0.3.0.dmg"},{"name":"TeleSave-0.3.0-windows-x64-setup.exe"}]}""";
        Assert.Null(ReleaseInfo.Parse(macOnly, UpdateChecker.IsWindowsAsset));
        var release = ReleaseInfo.Parse(both, UpdateChecker.IsWindowsAsset)!;
        Assert.Equal("0.3.0", release.Version);
        Assert.True(release.IsNewerThan("0.2.0"));
        Assert.False(release.IsNewerThan("0.10.0"));
    }
}

public class CredentialOrderTests
{
    private const string A = "0123456789abcdef0123456789abcdef", B = "fedcba9876543210fedcba9876543210";

    [Fact]
    public void BundledKeyCannotBeOverriddenByTheUser()
    {
        var store = new InMemorySecretStore();
        CredentialsResolver.Save(new ApiCredentials(111, A), store);
        var bundled = new ApiCredentials(222, B);
        Assert.Equal(222, CredentialsResolver.Resolve(store, null, bundled, new Dictionary<string, string?>())!.ApiId);
        Assert.Equal(333, CredentialsResolver.Resolve(store, new ApiCredentials(333, A), bundled, new Dictionary<string, string?>())!.ApiId);
        Assert.Equal(111, CredentialsResolver.Resolve(store, null, null, new Dictionary<string, string?>())!.ApiId);
    }
}
