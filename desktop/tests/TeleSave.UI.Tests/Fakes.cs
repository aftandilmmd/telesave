using Avalonia.Media.Imaging;
using TeleSave.Core;
using TeleSave.TdLib;
using TeleSave.UI;

namespace TeleSave.UI.Tests;

public sealed class FakePlatform : IPlatform
{
    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "TeleSaveUITests-" + Guid.NewGuid());
    public string DownloadsDirectory => DataDirectory;
    public ISecretStore Secrets { get; } = new InMemorySecretStore();
    public IPowerManager Power { get; } = new NoPower();
    public IReadOnlyList<string> PreferredLanguages { get; set; } = ["tr-TR"];
    public List<string> Opened { get; } = [];
    public (bool Enabled, string Title)? SendToMenu { get; private set; }
    public void SetSendToMenu(bool enabled, string title) => SendToMenu = (enabled, title);
    public bool IsAutostartEnabled { get; private set; }
    public void SetAutostart(bool enabled) => IsAutostartEnabled = enabled;
    public void Notify(string title, string body) { }
    public void OpenUrl(Uri url) => Opened.Add(url.ToString());
    public void RevealFile(string path) { }
    public Bitmap? FileIcon(string path, int size) => null;
    public ApiCredentials? BundledCredentials { get; set; }
    public (string? Urls, string? PublicKey) RemoteConfig => (null, null);
    public string AppVersion => "0.3.0";
    public (IReadOnlyList<string> Files, byte[]? Png) Clipboard { get; set; } = ([], null);
    public (IReadOnlyList<string> Files, byte[]? Png) ReadClipboard() => Clipboard;
    public FakeShortcuts FakeShortcuts { get; } = new();
    public IGlobalShortcuts Shortcuts => FakeShortcuts;

    private sealed class NoPower : IPowerManager
    {
        public IDisposable KeepAwake(string reason) => new Nothing();
        private sealed class Nothing : IDisposable { public void Dispose() { } }
    }
}

/// <summary>Telegram client with canned data: logs straight in, has a few chats and files.</summary>
public sealed class FakeClient(Action<TelegramEvent> onEvent, AuthState? initial = null) : ITelegramClient
{
    public static readonly TelegramUser User = new(42, "Aftandil", "Mammadov", "aftandil", false);
    private long _next = 1000;

    public void Start()
    {
        onEvent(new TelegramEvent.Auth(initial ?? new AuthState.Ready()));
        onEvent(new TelegramEvent.Connection(ConnectionState.Ready));
    }

    public Task RequestQrCodeAsync() => Task.CompletedTask;
    public Task SubmitPhoneNumberAsync(string phoneNumber) => Task.CompletedTask;
    public Task SubmitCodeAsync(string code) => Task.CompletedTask;
    public Task SubmitPasswordAsync(string password) => Task.CompletedTask;
    public Task LogOutAsync() => Task.CompletedTask;
    public Task<TelegramUser> FetchMeAsync() => Task.FromResult(User);

    public Task<IReadOnlyList<ChatSummary>> LoadChatsAsync(int limit) => Task.FromResult<IReadOnlyList<ChatSummary>>([
        new(1, "Ailə", ChatKind.Group, false, null),
        new(2, "TeleSave Duyurular", ChatKind.Channel, false, "telesave"),
        new(3, "Proje Ekibi", ChatKind.Group, true, null),
        new(4, "Elvin Həsənov", ChatKind.User, false, "elvin"),
    ]);

    public Task<bool> CanSendFilesAsync(long chatId) => Task.FromResult(chatId != 2);
    public Task<IReadOnlyList<ForumTopicSummary>> ForumTopicsAsync(long chatId) =>
        Task.FromResult<IReadOnlyList<ForumTopicSummary>>([new(1, "General", true, false), new(7, "Tasarım", false, false)]);
    public Task<string> DownloadFileAsync(int fileId) => Task.FromException<string>(new TelegramError(404, "none"));
    public Task<string> DownloadMessageFileAsync(long messageId, UploadDestination? destination) => DownloadFileAsync(0);
    public Task DeleteMessagesAsync(IReadOnlyList<long> messageIds, UploadDestination? destination) => Task.CompletedTask;

    public Task<ChatFilePage> SearchChatFilesAsync(UploadDestination? destination, string query, ChatFileFilter filter, ChatFileCursor? cursor, int limit)
    {
        var day = new DateTimeOffset(2026, 11, 14, 15, 15, 0, TimeSpan.FromHours(4));
        ChatFile F(long id, string name, ChatFileKind kind, long size, int ageDays, bool outgoing = true) =>
            new(id, (int)id, kind, name, size, day.AddDays(-ageDays), "", null, null, outgoing);
        IReadOnlyList<ChatFile> files =
        [
            F(9, "Kira Sözleşmesi 2026.pdf", ChatFileKind.Document, 2_400_000, 0),
            F(8, "IMG_4021.HEIC", ChatFileKind.Document, 3_100_000, 1),
            F(7, "Bakı sahili.mp4", ChatFileKind.Video, 48_000_000, 3),
            F(6, "Fatura_Kasım.xlsx", ChatFileKind.Document, 84_000, 6, outgoing: false),
            F(5, "Sunum.key", ChatFileKind.Document, 12_700_000, 9),
        ];
        var matching = files.Where(f => query.Length == 0 || SearchText.Matches(f.FileName, query)).ToList();
        return Task.FromResult(new ChatFilePage(matching, null));
    }

    public Task<IReadOnlyList<SentMessage>> SendAsync(IReadOnlyList<OutgoingFile> files, UploadDestination? destination) =>
        Task.FromResult<IReadOnlyList<SentMessage>>(files.Select(_ => new SentMessage(++_next, (int)_next)).ToList());
    public Task CancelAsync(IReadOnlyList<long> messageIds, UploadDestination? destination) => Task.CompletedTask;
    public void Close() { }
    public void Dispose() { }
}

public sealed class FakeShortcuts : IGlobalShortcuts
{
    public bool IsSupported => true;
    public event Action<ShortcutAction>? Pressed;
    public HashSet<ShortcutAction> Taken { get; } = [];
    public IReadOnlyDictionary<ShortcutAction, KeyShortcut> Registered { get; private set; } = new Dictionary<ShortcutAction, KeyShortcut>();
    public IReadOnlySet<ShortcutAction> Apply(IReadOnlyDictionary<ShortcutAction, KeyShortcut> shortcuts)
    {
        Registered = shortcuts.Where(p => !Taken.Contains(p.Key)).ToDictionary();
        return shortcuts.Keys.Where(Taken.Contains).ToHashSet();
    }
    public void Suspend(bool suspended) { }
    public void Press(ShortcutAction action) => Pressed?.Invoke(action);
}
