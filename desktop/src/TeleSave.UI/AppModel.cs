using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using TeleSave.Core;
using TeleSave.TdLib;

namespace TeleSave.UI;

/// <summary>App state and actions (port of the macOS AppModel). All members are used on the UI thread.</summary>
public sealed partial class AppModel : ObservableObject, IDisposable
{
    public enum AppPhase { NeedsCredentials, Running }

    public abstract record StatusIconState
    {
        public sealed record LoggedOut : StatusIconState;
        public sealed record Idle : StatusIconState;
        public sealed record Uploading(double Progress) : StatusIconState;
        public sealed record Error : StatusIconState;
    }

    public delegate ITelegramClient ClientFactory(TdLibTelegramClient.Configuration configuration, Action<TelegramEvent> onEvent);
    public delegate Task<ApiKeyValidator.Outcome> KeyValidator(ApiCredentials credentials);

    // MARK: Observable state

    [ObservableProperty] private AppPhase _phase = AppPhase.NeedsCredentials;
    [ObservableProperty] private AuthState _auth = new AuthState.Initializing();
    [ObservableProperty] private ConnectionState _connection = ConnectionState.Connecting;
    [ObservableProperty] private TelegramUser? _me;
    [ObservableProperty] private string? _avatarPath;
    [ObservableProperty] private IReadOnlyList<UploadItem> _items = [];
    [ObservableProperty] private double _bytesPerSecond;
    [ObservableProperty] private bool _networkAvailable = true;
    [ObservableProperty] private string? _remoteMessage;
    [ObservableProperty] private bool _updateRequired;
    [ObservableProperty] private ReleaseInfo? _availableUpdate;
    [ObservableProperty] private UploadDestination _destination = UploadDestination.SavedMessages;
    [ObservableProperty] private IReadOnlyList<UploadDestination> _recentDestinations = [];
    [ObservableProperty] private IReadOnlyList<ChatSummary> _chats = [];
    [ObservableProperty] private bool _isLoadingChats;
    [ObservableProperty] private string? _destinationError;
    [ObservableProperty] private bool _chatFilesLoading;
    [ObservableProperty] private bool _chatFilesComplete;
    [ObservableProperty] private string? _chatFilesError;
    [ObservableProperty] private string? _authError;
    [ObservableProperty] private string? _notice;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isValidatingKey;
    [ObservableProperty] private AppSettings _settings;

    public ObservableCollection<ChatFile> ChatFiles { get; } = [];
    public HashSet<Guid> BusyItems { get; } = [];
    public HashSet<long> BusyChatFiles { get; } = [];

    /// <summary>Asks the window layer to show the panel (e.g. after files arrive from Explorer).</summary>
    public event Action? PanelRequested;
    /// <summary>Asks the window layer to show or hide the panel (global shortcut).</summary>
    public event Action? PanelToggleRequested;

    // MARK: Dependencies and paths

    public IPlatform Platform { get; }
    private readonly Action<Action> _post;
    private readonly ClientFactory _clientFactory;
    private readonly KeyValidator _validator;
    private readonly DropIntake _intake;
    private ITelegramClient? _client;
    private UploadQueue? _queue;
    private ApiCredentials? _activeCredentials;
    private int _clientGeneration;
    private readonly RemoteConfigService? _remoteConfig;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TransferRate _rate = new();
    private Dictionary<Guid, UploadStatus> _previousStates = new();
    private int _pendingDone;
    private readonly List<string> _pendingFailed = [];
    private CancellationTokenSource? _notifyDebounce;
    private CancellationTokenSource? _noticeTimer;
    private SentMessageRegistry _sent;

    private string Root => Platform.DataDirectory;
    private string SettingsPath => Path.Combine(Root, "settings.json");
    private string QueuePath => Path.Combine(Root, "queue.json");
    private string SentPath => Path.Combine(Root, "sent-messages.json");
    private string DestinationsPath => Path.Combine(Root, "destinations.json");
    private string StagingPath => Path.Combine(Root, "staging");
    private const string DatabaseKeyAccount = "tdlib-db-key";

    public string AppVersion => Platform.AppVersion;

    /// <param name="backgroundChecks">Poll remote config and GitHub releases (off in tests).</param>
    public AppModel(IPlatform platform, Action<Action> post, ClientFactory? clientFactory = null, KeyValidator? validator = null, bool start = true, bool backgroundChecks = true)
    {
        Platform = platform;
        _post = post;
        _clientFactory = clientFactory ?? ((config, onEvent) => new TdLibTelegramClient(config, onEvent));
        _validator = validator ?? (c => ApiKeyValidator.ValidateAsync(c));
        L10n.PreferredLanguages = () => platform.PreferredLanguages;
        _settings = AppSettings.Load(Path.Combine(platform.DataDirectory, "settings.json"));
        L10n.SelectedCode = _settings.Language;
        _intake = new DropIntake(Path.Combine(platform.DataDirectory, "staging"));
        _sent = SentMessageRegistry.Load(Path.Combine(platform.DataDirectory, "sent-messages.json"));

        var (urls, key) = platform.RemoteConfig;
        if (RemoteConfigService.ParseUrls(urls) is { Count: > 0 } parsed && key is not null && RemoteConfigVerifier.FromBase64(key) is { } verifier)
            _remoteConfig = new RemoteConfigService(parsed, verifier, platform.Secrets);
        ApplyRemoteNotices(_remoteConfig?.Initial);

        if (!start) return;
        NetworkAvailable = NetworkInterface.GetIsNetworkAvailable();
        NetworkChange.NetworkAvailabilityChanged += OnNetworkChanged;
        if (CredentialsResolver.Resolve(platform.Secrets, _remoteConfig?.Initial?.Credentials, platform.BundledCredentials) is { } credentials)
            StartClient(credentials);
        if (!backgroundChecks) return;
        _ = PollRemoteConfigAsync(_lifetime.Token);
        _ = PollUpdatesAsync(_lifetime.Token);
    }

    // MARK: Lifecycle

    private void StartClient(ApiCredentials credentials)
    {
        Phase = AppPhase.Running;
        Auth = new AuthState.Initializing();
        _activeCredentials = credentials;
        var generation = ++_clientGeneration;
        var configuration = new TdLibTelegramClient.Configuration(
            Path.Combine(Root, "tdlib", "db"), Path.Combine(Root, "tdlib", "files"), Path.Combine(Root, "logs", "tdlib.log"),
            credentials, DatabaseKey(), AppVersion);
        // Events of an older client (before a restart) are dropped.
        var client = _clientFactory(configuration, e => _post(() => { if (generation == _clientGeneration) HandleEvent(e); }));
        _client = client;
        if (_queue is null)
        {
            _queue = new UploadQueue(client, new QueueStore(QueuePath), Settings.QueueSettings, power: Platform.Power);
            _queue.Changed += items => _post(() => ApplyItems(items));
            ApplyItems(_queue.Items);
        }
        else
        {
            _queue.ReplaceTransport(client);
        }
        client.Start();
    }

    private async Task StopClientAsync()
    {
        _clientGeneration++;
        var old = _client;
        _client = null;
        _activeCredentials = null;
        Me = null;
        Auth = new AuthState.Initializing();
        UpdateQueuePause();
        if (old is not null) await Task.Run(old.Close).ConfigureAwait(true);
    }

    private async Task RestartAsync(ApiCredentials credentials)
    {
        await StopClientAsync();
        StartClient(credentials);
    }

    public void Shutdown()
    {
        _lifetime.Cancel();
        _client?.Close();
    }

    public void Dispose()
    {
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkChanged;
        Shutdown();
        _queue?.Dispose();
    }

    private byte[] DatabaseKey()
    {
        if (Platform.Secrets.Read(DatabaseKeyAccount) is { Length: 32 } key) return key;
        var fresh = RandomNumberGenerator.GetBytes(32);
        Platform.Secrets.Write(DatabaseKeyAccount, fresh);
        return fresh;
    }

    private void OnNetworkChanged(object? sender, NetworkAvailabilityEventArgs e) => _post(() =>
    {
        NetworkAvailable = e.IsAvailable;
        UpdateQueuePause();
    });

    // MARK: Events

    private void HandleEvent(TelegramEvent telegramEvent)
    {
        switch (telegramEvent)
        {
            case TelegramEvent.Auth auth:
                if (auth.State != Auth) AuthError = null;
                Auth = auth.State;
                if (auth.State.IsReady) _ = LoadMeAsync();
                else { Me = null; AvatarPath = null; }
                UpdateQueuePause();
                break;
            case TelegramEvent.Connection connection:
                Connection = connection.State;
                break;
            default:
                _queue?.Handle(telegramEvent);
                break;
        }
    }

    private async Task LoadMeAsync()
    {
        if (_client is not { } client) return;
        try
        {
            var user = await client.FetchMeAsync();
            if (user.Id != Me?.Id) RestoreDestinations(user.Id);
            Me = user;
            RecordSent(Items);
            UpdateQueuePause();
            if (user.PhotoFileId is { } photo)
            {
                try { AvatarPath = await client.DownloadFileAsync(photo); } catch (Exception) { }
            }
        }
        catch (Exception e)
        {
            ShowNotice(L10n.T("Hesap bilgisi alınamadı: %@", e.Message));
        }
    }

    private void UpdateQueuePause() => _queue?.SetPaused(!(Auth.IsReady && Me is not null && NetworkAvailable));

    private void ApplyItems(IReadOnlyList<UploadItem> snapshot)
    {
        Items = snapshot;
        var inFlight = snapshot.Where(i => i.Status == UploadStatus.Uploading).ToList();
        if (inFlight.Count == 0) _rate.Reset();
        else _rate.Sample(inFlight.Sum(i => i.ShownProgress * i.Size), DateTimeOffset.Now);
        BytesPerSecond = _rate.BytesPerSecond;

        RecordSent(snapshot);
        var newlyDone = false;
        foreach (var item in snapshot)
        {
            if (!_previousStates.TryGetValue(item.Id, out var before) || before is UploadStatus.Done or UploadStatus.Failed or UploadStatus.Cancelled) continue;
            if (item.Status == UploadStatus.Done) { _pendingDone++; newlyDone = true; }
            else if (item.Status == UploadStatus.Failed) _pendingFailed.Add(item.DisplayName);
        }
        _previousStates = snapshot.ToDictionary(i => i.Id, i => i.Status);
        if (_pendingDone > 0 || _pendingFailed.Count > 0) ScheduleNotification();
        if (newlyDone && Settings.ShowChatFiles) ScheduleChatFilesRefresh();
        OnPropertyChanged(nameof(StatusIcon));
        OnPropertyChanged(nameof(RemainingTime));
    }

    // MARK: Notifications

    private void ScheduleNotification()
    {
        _notifyDebounce?.Cancel();
        var cts = _notifyDebounce = new CancellationTokenSource();
        _ = Task.Delay(1500, cts.Token).ContinueWith(t => { if (!t.IsCanceled) _post(PostNotification); }, TaskScheduler.Default);
    }

    private void PostNotification()
    {
        var done = _pendingDone;
        var failed = _pendingFailed.ToList();
        _pendingDone = 0;
        _pendingFailed.Clear();
        if (!Settings.NotificationsEnabled) return;
        var lines = new List<string>();
        if (done > 0) lines.Add(L10n.T("%@ dosya yüklendi.", done.ToString()));
        if (failed.Count > 0) lines.Add(L10n.T("%@ dosya yüklenemedi: %@", failed.Count.ToString(), string.Join(", ", failed.Take(3))));
        try { Platform.Notify("TeleSave", string.Join("\n", lines)); } catch (Exception) { }
    }

    public void ShowNotice(string text)
    {
        Notice = text;
        _noticeTimer?.Cancel();
        var cts = _noticeTimer = new CancellationTokenSource();
        _ = Task.Delay(6000, cts.Token).ContinueWith(t => { if (!t.IsCanceled) _post(() => Notice = null); }, TaskScheduler.Default);
    }

    // MARK: Status

    public StatusIconState StatusIcon
    {
        get
        {
            if (Phase != AppPhase.Running || !Auth.IsReady) return new StatusIconState.LoggedOut();
            var active = Items.Where(i => i.Status is UploadStatus.Uploading or UploadStatus.Queued).ToList();
            if (active.Count > 0)
            {
                double total = active.Sum(i => (double)i.Size);
                return new StatusIconState.Uploading(total > 0 ? active.Sum(i => i.ShownProgress * i.Size) / total : 0);
            }
            return Items.Any(i => i.Status == UploadStatus.Failed) ? new StatusIconState.Error() : new StatusIconState.Idle();
        }
    }

    public TimeSpan? RemainingTime => _rate.Eta(Items.Where(i => i.Status is UploadStatus.Queued or UploadStatus.Uploading)
        .Sum(i => (1 - i.ShownProgress) * i.Size));

    public bool CanUpload => Auth.IsReady && Me is not null;

    public bool HasBundledCredentials => Platform.BundledCredentials?.IsValid == true;

    partial void OnAuthChanged(AuthState value) { OnPropertyChanged(nameof(CanUpload)); OnPropertyChanged(nameof(StatusIcon)); }
    partial void OnMeChanged(TelegramUser? value) => OnPropertyChanged(nameof(CanUpload));
    partial void OnPhaseChanged(AppPhase value) => OnPropertyChanged(nameof(StatusIcon));

    // MARK: Uploads

    /// <param name="fromShell">Files came from Explorer / the taskbar / the edge shelf rather than the open panel.</param>
    /// <param name="fromShortcut">Sent with the global shortcut: the panel only opens if there is something to see.</param>
    public void HandleDrop(IReadOnlyList<string> paths, bool fromShell = false, bool temporary = false, bool fromShortcut = false)
    {
        if (paths.Count == 0) return;
        if (!CanUpload || Me is null || _queue is null)
        {
            ShowNotice(L10n.T("Yüklemek için önce Telegram'a giriş yapın."));
            PanelRequested?.Invoke();
            return;
        }
        var me = Me;
        var settings = Settings;
        var destination = Destination;
        var queue = _queue;
        _ = Task.Run(() => _intake.Prepare(paths, me.MaxUploadSize, settings.FolderMode, temporary)).ContinueWith(t => _post(() =>
        {
            if (t.Result is not { } result) return;
            queue.Enqueue(result.Accepted, destination);
            if (result.Rejected.Count > 0) ShowNotice(string.Join("\n", result.Rejected.Select(r => r.Message)));
            // Always show the panel when files go somewhere other than Saved Messages, so the target is visible.
            var attention = result.Rejected.Count > 0 || !destination.IsSavedMessages;
            if ((fromShell && (settings.OpenPanelOnDrop || attention)) || (fromShortcut && attention)) PanelRequested?.Invoke();
        }), TaskScheduler.Default);
    }

    /// <summary>An image from the clipboard (PNG bytes): saved as a temporary file and uploaded.</summary>
    public void HandlePastedImage(byte[] png, bool fromShortcut = false)
    {
        try
        {
            var folder = Path.Combine(StagingPath, Guid.NewGuid().ToString());
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, L10n.T("Yapıştırılan görsel %@.png", DateTime.Now.ToString("yyyy-MM-dd 'at' HH.mm.ss", System.Globalization.CultureInfo.InvariantCulture)));
            File.WriteAllBytes(file, png);
            HandleDrop([file], temporary: true, fromShortcut: fromShortcut);
        }
        catch (Exception e)
        {
            ShowNotice(L10n.T("Görsel kaydedilemedi: %@", e.Message));
        }
    }

    /// <summary>Ctrl+V / "Paste from clipboard": copied files, or a copied image / screenshot. False if there was nothing.</summary>
    public bool PasteFromClipboard(bool fromShortcut = false)
    {
        var (files, png) = Platform.ReadClipboard();
        if (files.Count > 0) HandleDrop(files, fromShortcut: fromShortcut);
        else if (png is not null) HandlePastedImage(png, fromShortcut);
        else
        {
            ShowNotice(L10n.T("Panoda gönderilecek dosya ya da görsel yok."));
            return false;
        }
        return true;
    }

    // MARK: Global shortcuts

    /// <summary>Actions whose shortcut another app already holds; shown in Settings.</summary>
    [ObservableProperty] private IReadOnlySet<ShortcutAction> _failedShortcuts = new HashSet<ShortcutAction>();
    private bool _shortcutsStarted;

    /// <summary>Registers the shortcuts from the settings (called once by the shell).</summary>
    public void StartShortcuts()
    {
        if (!_shortcutsStarted)
        {
            _shortcutsStarted = true;
            Platform.Shortcuts.Pressed += action => _post(() => Perform(action));
        }
        ApplyShortcuts();
    }

    private void ApplyShortcuts()
    {
        if (!_shortcutsStarted) return;
        try { FailedShortcuts = Platform.Shortcuts.Apply(Settings.Shortcuts); }
        catch (Exception) { FailedShortcuts = Settings.Shortcuts.Keys.ToHashSet(); }
    }

    public void Perform(ShortcutAction action)
    {
        if (action == ShortcutAction.TogglePanel) PanelToggleRequested?.Invoke();
        else UploadClipboardFromShortcut();
    }

    /// <summary>Uploads copied files (or a copied image) without opening the panel, unless there is something to see.</summary>
    public void UploadClipboardFromShortcut()
    {
        if (!CanUpload)
        {
            ShowNotice(L10n.T("Yüklemek için önce Telegram'a giriş yapın."));
            PanelRequested?.Invoke();
            return;
        }
        if (!PasteFromClipboard(fromShortcut: true)) PanelRequested?.Invoke();
    }

    /// <summary>Warning shown under a shortcut in Settings, if it will not work as expected.</summary>
    public string? ShortcutConflict(ShortcutAction action)
    {
        if (!Settings.Shortcuts.ContainsKey(action)) return null;
        if (Settings.UploadShortcut is not null && Settings.UploadShortcut == Settings.PanelShortcut) return L10n.T("İki işlem aynı kısayolu kullanıyor.");
        return FailedShortcuts.Contains(action) ? L10n.T("Bu kısayol başka bir uygulama tarafından kullanılıyor.") : null;
    }

    public void Cancel(UploadItem item) => _queue?.Cancel(item.Id);
    public void Retry(UploadItem item) => _queue?.Retry(item.Id);
    public void Remove(UploadItem item) => _queue?.Remove(item.Id);
    public void ClearFinished() => _queue?.ClearFinished();

    public void OpenInTelegram(UploadDestination? target = null)
    {
        if (Me is null) return;
        string link;
        if (target is { IsSavedMessages: false }) link = target.Username is { } u ? $"tg://resolve?domain={u}" : "tg://";
        else link = Me.Username is { } name ? $"tg://resolve?domain={name}" : $"tg://openmessage?user_id={Me.Id}";
        try { Platform.OpenUrl(new Uri(link)); }
        catch (Exception) { Platform.OpenUrl(new Uri("https://web.telegram.org/")); }
    }

    public void Download(UploadItem item)
    {
        if (item.Status != UploadStatus.Done || item.MessageId is not { } messageId || _client is not { } client || !BusyItems.Add(item.Id)) return;
        OnPropertyChanged(nameof(BusyItems));
        _ = SaveToDownloadsAsync(() => client.DownloadMessageFileAsync(messageId, item.Destination), item.DisplayName,
            () => { BusyItems.Remove(item.Id); OnPropertyChanged(nameof(BusyItems)); });
    }

    public void DeleteFromTelegram(UploadItem item)
    {
        if (item.Status != UploadStatus.Done || item.MessageId is not { } messageId || _client is not { } client || !BusyItems.Add(item.Id)) return;
        OnPropertyChanged(nameof(BusyItems));
        _ = RunAsync(async () =>
        {
            try
            {
                await client.DeleteMessagesAsync([messageId], item.Destination);
                ForgetSent(item.Destination?.ChatId, messageId);
                _queue?.Remove(item.Id);
                RemoveChatFile(messageId);
                ShowNotice(L10n.T("%@ Telegram'dan silindi.", item.DisplayName));
            }
            catch (Exception e) { ShowNotice(L10n.T("%@ silinemedi: %@", item.DisplayName, e.Message)); }
            finally { BusyItems.Remove(item.Id); OnPropertyChanged(nameof(BusyItems)); }
        });
    }

    private async Task SaveToDownloadsAsync(Func<Task<string>> download, string fileName, Action done)
    {
        try
        {
            var path = await download();
            var target = MessageFileLocator.UniquePath(Platform.DownloadsDirectory, fileName);
            await Task.Run(() => File.Copy(path, target));
            Platform.RevealFile(target);
        }
        catch (Exception e)
        {
            ShowNotice(L10n.T("%@ indirilemedi: %@", fileName, e.Message));
        }
        finally { done(); }
    }

    private static async Task RunAsync(Func<Task> action)
    {
        try { await action(); } catch (Exception) { }
    }

    // MARK: Auth

    private void Perform(Func<ITelegramClient, Task> action)
    {
        if (_client is not { } client) return;
        IsBusy = true;
        AuthError = null;
        _ = RunAsync(async () =>
        {
            try { await action(client); }
            catch (Exception e) { AuthError = AuthErrorText.Describe(e); }
            finally { IsBusy = false; }
        });
    }

    public void RequestQrCode() => Perform(c => c.RequestQrCodeAsync());
    public void SubmitPhone(string phone) => Perform(c => c.SubmitPhoneNumberAsync(new string(phone.Where(ch => char.IsDigit(ch) || ch == '+').ToArray())));
    public void SubmitCode(string code) => Perform(c => c.SubmitCodeAsync(new string(code.Where(char.IsDigit).ToArray())));
    public void SubmitPassword(string password) => Perform(c => c.SubmitPasswordAsync(password));
    public void LogOut() => Perform(c => c.LogOutAsync());

    // MARK: Credentials

    public int? CurrentApiId => _activeCredentials?.ApiId;

    /// <summary>Checks the key with Telegram first; only an accepted key replaces the current one.</summary>
    public async Task<bool> SaveCredentialsAsync(string apiId, string apiHash)
    {
        var credentials = new ApiCredentials(int.TryParse(apiId.Trim(), out var id) ? id : 0, apiHash.Trim().ToLowerInvariant());
        if (!credentials.IsValid)
        {
            AuthError = L10n.T("api_id bir sayı, api_hash 32 karakterlik bir hex dizesi olmalı.");
            return false;
        }
        AuthError = null;
        if (credentials == _activeCredentials) return true;
        IsValidatingKey = true;
        var outcome = await _validator(credentials);
        IsValidatingKey = false;
        switch (outcome)
        {
            case ApiKeyValidator.Outcome.Invalid:
                AuthError = L10n.T("Bu anahtar Telegram tarafından reddedildi. api_id ve api_hash değerlerini kontrol edin.");
                return false;
            case ApiKeyValidator.Outcome.Failed failed:
                AuthError = L10n.T("Anahtar doğrulanamadı: %@", failed.Reason);
                return false;
        }
        if (!CredentialsResolver.Save(credentials, Platform.Secrets))
        {
            AuthError = L10n.T("Anahtar Keychain'e kaydedilemedi.");
            return false;
        }
        await RestartAsync(credentials);
        return true;
    }

    // MARK: Settings

    public void UpdateSettings(Func<AppSettings, AppSettings> change)
    {
        var old = Settings;
        var updated = change(old);
        if (updated == old) return;
        Settings = updated;
        updated.Save(SettingsPath);
        L10n.SelectedCode = updated.Language;
        _queue?.UpdateSettings(updated.QueueSettings);
        if (updated.ContextMenuEnabled != old.ContextMenuEnabled || updated.Language != old.Language) ApplyContextMenu();
        if (updated.UploadShortcut != old.UploadShortcut || updated.PanelShortcut != old.PanelShortcut) ApplyShortcuts();
    }

    public void ApplyContextMenu()
    {
        try { Platform.SetSendToMenu(Settings.ContextMenuEnabled, L10n.T("TeleSave'e gönder")); } catch (Exception) { }
    }

    /// <summary>Launch at login is on by default: enabled once on first start, then left to the user.</summary>
    public void EnableAutostartOnFirstLaunch()
    {
        if (Settings.AutostartConfigured) return;
        try { Platform.SetAutostart(true); } catch (Exception) { }
        UpdateSettings(s => s with { AutostartConfigured = true });
    }

    // MARK: Remote config and updates

    private async Task PollRemoteConfigAsync(CancellationToken token)
    {
        if (_remoteConfig is null) return;
        while (!token.IsCancellationRequested)
        {
            if (await _remoteConfig.RefreshAsync() is { } config) _post(() => _ = ApplyRemoteAsync(config));
            try { await Task.Delay(TimeSpan.FromHours(6), token); } catch (TaskCanceledException) { return; }
        }
    }

    private async Task ApplyRemoteAsync(RemoteConfig config)
    {
        ApplyRemoteNotices(config);
        if (CredentialsResolver.Resolve(Platform.Secrets, config.Credentials, Platform.BundledCredentials) is not { } credentials
            || credentials == _activeCredentials) return;
        if (_client is null) StartClient(credentials);
        else await RestartAsync(credentials);
    }

    private void ApplyRemoteNotices(RemoteConfig? config)
    {
        RemoteMessage = string.IsNullOrEmpty(config?.Message) ? null : config!.Message;
        UpdateRequired = config?.RequiresUpdate(AppVersion) ?? false;
    }

    private async Task PollUpdatesAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var release = await UpdateChecker.LatestReleaseAsync(UpdateChecker.IsWindowsAsset);
            if (release is not null) _post(() => AvailableUpdate = release.IsNewerThan(AppVersion) ? release : null);
            try { await Task.Delay(TimeSpan.FromHours(24), token); } catch (TaskCanceledException) { return; }
        }
    }

    // MARK: Sent-message registry

    private void RecordSent(IEnumerable<UploadItem> snapshot)
    {
        if (Me is null) return;
        var changed = false;
        foreach (var item in snapshot.Where(i => i.Status == UploadStatus.Done && i.MessageId is not null))
            changed |= _sent.Insert(Me.Id, item.Destination?.ChatId, item.MessageId!.Value);
        if (changed) _sent.Save(SentPath);
    }

    private void ForgetSent(long? chatId, long messageId)
    {
        if (Me is not null && _sent.Remove(Me.Id, chatId, messageId)) _sent.Save(SentPath);
    }

    public bool IsFromTeleSave(ChatFile file) => Me is not null && _sent.Contains(Me.Id, Destination.ChatId, file.MessageId);

    // MARK: Chat files

    private ChatFileCursor? _chatFilesCursor;
    private int _chatFilesGeneration;
    private string _chatFilesQuery = "";
    private ChatFileFilter _chatFilesFilter = ChatFileFilter.All;
    private CancellationTokenSource? _chatFilesRefresh;
    private const int ChatFilesPageSize = 40;

    public async Task ReloadChatFilesAsync(string query, ChatFileFilter filter)
    {
        _chatFilesGeneration++;
        _chatFilesQuery = query;
        _chatFilesFilter = filter;
        ChatFiles.Clear();
        _chatFilesCursor = null;
        ChatFilesComplete = false;
        ChatFilesError = null;
        ChatFilesLoading = false;
        await LoadMoreChatFilesAsync();
    }

    public async Task LoadMoreChatFilesAsync()
    {
        if (_client is not { } client || ChatFilesLoading || ChatFilesComplete) return;
        var generation = _chatFilesGeneration;
        ChatFilesLoading = true;
        try
        {
            // A search may scan several pages before anything matches; keep going a while so an empty
            // first page does not look like "no results". Afterwards the user can continue manually.
            var added = 0;
            var rounds = 0;
            do
            {
                var page = await client.SearchChatFilesAsync(Destination.IsSavedMessages ? null : Destination, _chatFilesQuery,
                    _chatFilesFilter, _chatFilesCursor, ChatFilesPageSize);
                if (generation != _chatFilesGeneration) return;
                var known = ChatFiles.Select(f => f.Id).ToHashSet();
                foreach (var file in page.Files.Where(f => !known.Contains(f.Id))) { ChatFiles.Add(file); added++; }
                _chatFilesCursor = page.Cursor;
                ChatFilesComplete = page.IsLastPage;
                rounds++;
            } while (added == 0 && !ChatFilesComplete && rounds < 6);
        }
        catch (Exception e)
        {
            if (generation == _chatFilesGeneration) ChatFilesError = L10n.T("Dosyalar yüklenemedi: %@", e.Message);
        }
        finally
        {
            if (generation == _chatFilesGeneration) ChatFilesLoading = false;
        }
    }

    private void ScheduleChatFilesRefresh()
    {
        _chatFilesRefresh?.Cancel();
        var cts = _chatFilesRefresh = new CancellationTokenSource();
        _ = Task.Delay(1000, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) _post(() => _ = ReloadChatFilesAsync(_chatFilesQuery, _chatFilesFilter));
        }, TaskScheduler.Default);
    }

    public async Task<string?> ThumbnailPathAsync(ChatFile file)
    {
        if (_client is not { } client || file.ThumbnailFileId is not { } id) return null;
        try { return await client.DownloadFileAsync(id); } catch (Exception) { return null; }
    }

    public void Download(ChatFile file)
    {
        if (_client is not { } client || !BusyChatFiles.Add(file.Id)) return;
        OnPropertyChanged(nameof(BusyChatFiles));
        _ = SaveToDownloadsAsync(() => client.DownloadFileAsync(file.FileId), file.FileName,
            () => { BusyChatFiles.Remove(file.Id); OnPropertyChanged(nameof(BusyChatFiles)); });
    }

    /// <summary>Saved Messages: everything is yours. Elsewhere only your own messages are offered for deletion.</summary>
    public bool CanDelete(ChatFile file) => Destination.IsSavedMessages || file.IsOutgoing;

    public void Delete(ChatFile file)
    {
        if (_client is not { } client || !BusyChatFiles.Add(file.Id)) return;
        OnPropertyChanged(nameof(BusyChatFiles));
        var target = Destination.IsSavedMessages ? null : Destination;
        _ = RunAsync(async () =>
        {
            try
            {
                await client.DeleteMessagesAsync([file.MessageId], target);
                RemoveChatFile(file.MessageId);
                ForgetSent(target?.ChatId, file.MessageId);
                if (Items.FirstOrDefault(i => i.Status == UploadStatus.Done && i.MessageId == file.MessageId) is { } item) _queue?.Remove(item.Id);
            }
            catch (Exception e) { ShowNotice(L10n.T("%@ silinemedi: %@", file.FileName, e.Message)); }
            finally { BusyChatFiles.Remove(file.Id); OnPropertyChanged(nameof(BusyChatFiles)); }
        });
    }

    private void RemoveChatFile(long messageId)
    {
        if (ChatFiles.FirstOrDefault(f => f.MessageId == messageId) is { } existing) ChatFiles.Remove(existing);
    }

    /// <summary>Queue items not (yet) in the chat list: in progress, failed, or just finished.</summary>
    public IReadOnlyList<UploadItem> PendingUploads
    {
        get
        {
            var listed = ChatFiles.Select(f => f.MessageId).ToHashSet();
            var shown = Destination.IsSavedMessages ? null : Destination;
            return Items.Reverse().Where(i => i.Destination == shown && i.Status switch
            {
                UploadStatus.Queued or UploadStatus.Uploading or UploadStatus.Failed => true,
                UploadStatus.Done => !listed.Contains(i.MessageId ?? 0) && (i.FinishedAt ?? DateTimeOffset.MinValue) > DateTimeOffset.Now.AddSeconds(-60),
                _ => false
            }).ToList();
        }
    }

    // MARK: Destinations

    private sealed record StoredDestinations(long OwnerId, UploadDestination Current, List<UploadDestination> Recents);

    private void RestoreDestinations(long userId)
    {
        Chats = [];
        _chatFilesGeneration++;
        ChatFiles.Clear();
        try
        {
            var stored = File.Exists(DestinationsPath) ? JsonSerializer.Deserialize<StoredDestinations>(File.ReadAllText(DestinationsPath)) : null;
            if (stored?.OwnerId == userId)
            {
                Destination = stored.Current;
                RecentDestinations = stored.Recents;
                return;
            }
        }
        catch (Exception) { }
        Destination = UploadDestination.SavedMessages;
        RecentDestinations = [];
    }

    private void SaveDestinations()
    {
        if (Me is null) return;
        try { File.WriteAllText(DestinationsPath, JsonSerializer.Serialize(new StoredDestinations(Me.Id, Destination, RecentDestinations.ToList()))); }
        catch (Exception) { }
    }

    public async Task RefreshChatsAsync()
    {
        if (_client is not { } client || IsLoadingChats) return;
        IsLoadingChats = true;
        try { Chats = (await client.LoadChatsAsync(100)).Where(c => c.Kind != ChatKind.SavedMessages).ToList(); }
        catch (Exception e) { DestinationError = L10n.T("Sohbetler yüklenemedi: %@", e.Message); }
        finally { IsLoadingChats = false; }
    }

    public async Task<IReadOnlyList<ForumTopicSummary>> ForumTopicsAsync(ChatSummary chat)
    {
        if (_client is not { } client) return [];
        try { return (await client.ForumTopicsAsync(chat.Id)).Where(t => !t.IsClosed).ToList(); }
        catch (Exception e) { DestinationError = L10n.T("Konular yüklenemedi: %@", e.Message); return []; }
    }

    /// <summary>Selects a chat (and forum topic) after checking the user may send files there.</summary>
    public async Task<bool> ChooseAsync(ChatSummary chat, ForumTopicSummary? topic = null)
    {
        if (_client is not { } client) return false;
        DestinationError = null;
        try
        {
            if (!await client.CanSendFilesAsync(chat.Id))
            {
                DestinationError = chat.Kind == ChatKind.Channel
                    ? L10n.T("%@: bu kanalda gönderi yetkiniz yok.", chat.Title)
                    : L10n.T("%@: bu sohbete dosya gönderme izniniz yok.", chat.Title);
                return false;
            }
        }
        catch (Exception e)
        {
            DestinationError = L10n.T("İzin kontrol edilemedi: %@", e.Message);
            return false;
        }
        Select(new UploadDestination(chat.Id, topic?.Id, topic is null ? chat.Title : $"{chat.Title} › {topic.Name}", chat.Username));
        return true;
    }

    public void Select(UploadDestination destination)
    {
        Destination = destination;
        if (!destination.IsSavedMessages)
        {
            RecentDestinations = new[] { destination }
                .Concat(RecentDestinations.Where(d => !(d.ChatId == destination.ChatId && d.ForumTopicId == destination.ForumTopicId)))
                .Take(5).ToList();
        }
        SaveDestinations();
    }
}
