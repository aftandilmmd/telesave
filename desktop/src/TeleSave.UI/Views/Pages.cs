using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using QRCoder;
using TeleSave.Core;

namespace TeleSave.UI.Views;

// MARK: - Chat files

/// <summary>
/// Every file in the destination chat, with local accent-insensitive search and type filters. Uploads still
/// in progress (or failed) are pinned on top, since they are not in the chat yet.
/// </summary>
public sealed class ChatFilesView : ModelPage
{
    private readonly TextBox _search = new() { Watermark = L10n.T("Dosya ara") };
    private readonly ComboBox _filter = new() { MinWidth = 96 };
    private readonly ContentControl _content = new();
    private readonly StackPanel _rows = new();
    private readonly ScrollViewer _scroll;
    private CancellationTokenSource? _debounce;
    private string _reloadKey = "";

    private static readonly (ChatFileFilter Filter, string Key)[] Filters =
        [(ChatFileFilter.All, "Tümü"), (ChatFileFilter.Documents, "Belge"), (ChatFileFilter.Images, "Resim"), (ChatFileFilter.Videos, "Video"), (ChatFileFilter.Audio, "Ses")];

    public ChatFilesView(AppModel model) : base(model)
    {
        _filter.ItemsSource = Filters.Select(f => L10n.T(f.Key)).ToList();
        _filter.SelectedIndex = 0;
        var top = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(12, 8) };
        top.Children.Add(_search);
        Grid.SetColumn(_filter, 1);
        _filter.Margin = new Thickness(8, 0, 0, 0);
        top.Children.Add(_filter);
        _scroll = new ScrollViewer { Content = _rows };
        _scroll.ScrollChanged += (_, _) =>
        {
            if (_scroll.Offset.Y + _scroll.Viewport.Height >= _scroll.Extent.Height - 80) _ = Model.LoadMoreChatFilesAsync();
        };
        var layout = new DockPanel();
        var header = new StackPanel();
        header.Children.Add(top);
        header.Children.Add(Ui.Divider());
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);
        layout.Children.Add(_content);
        Content = layout;
        _search.TextChanged += (_, _) => ScheduleReload(300);
        _filter.SelectionChanged += (_, _) => ScheduleReload(0);
        AttachedToVisualTree += (_, _) => ScheduleReload(0);
        Model.ChatFiles.CollectionChanged += (_, _) => Dispatcher.UIThread.Post(Refresh, DispatcherPriority.Background);
    }

    private ChatFileFilter SelectedFilter => Filters[Math.Max(0, _filter.SelectedIndex)].Filter;

    private void ScheduleReload(int delayMs)
    {
        _debounce?.Cancel();
        var cts = _debounce = new CancellationTokenSource();
        Dispatcher.UIThread.Post(async () =>
        {
            if (delayMs > 0) { try { await Task.Delay(delayMs, cts.Token); } catch (TaskCanceledException) { return; } }
            _reloadKey = ReloadKey();
            await Model.ReloadChatFilesAsync((_search.Text ?? "").Trim(), SelectedFilter);
        });
    }

    private string ReloadKey() => $"{Model.Me?.Id}|{Model.Destination.ChatId}|{Model.Destination.ForumTopicId}";

    protected override void Refresh()
    {
        // Destination or account changed: list a different chat.
        if (_reloadKey.Length > 0 && ReloadKey() != _reloadKey) ScheduleReload(0);
        var pending = Model.PendingUploads;
        var query = (_search.Text ?? "").Trim();
        if (pending.Count == 0 && Model.ChatFiles.Count == 0)
        {
            if (Model.ChatFilesError is { } error) _content.Content = Placeholder(error, Glyph.Warning);
            else if (Model.ChatFilesLoading) _content.Content = new ProgressBar { IsIndeterminate = true, Width = 80, VerticalAlignment = VerticalAlignment.Center };
            else if (!Model.ChatFilesComplete)
            {
                // Scanned a while without a match, but older messages remain.
                var stack = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
                stack.Children.Add(Ui.Text(L10n.T("Henüz eşleşme yok"), brush: Ui.Secondary));
                stack.Children.Add(new Button { Content = L10n.T("Aramaya devam et") }.With(b => b.Click += (_, _) => _ = Model.LoadMoreChatFilesAsync()));
                _content.Content = stack;
            }
            else _content.Content = Placeholder(query.Length == 0 ? L10n.T("Bu sohbette dosya yok") : L10n.T("Sonuç yok"), Glyph.Search);
            return;
        }
        _rows.Children.Clear();
        foreach (var item in pending)
        {
            _rows.Children.Add(new QueueRow(Model, item));
            _rows.Children.Add(Ui.Divider().With(d => d.Margin = new Thickness(60, 0, 0, 0)));
        }
        foreach (var file in Model.ChatFiles)
        {
            _rows.Children.Add(new ChatFileRow(Model, file));
            _rows.Children.Add(Ui.Divider().With(d => d.Margin = new Thickness(60, 0, 0, 0)));
        }
        if (Model.ChatFilesLoading) _rows.Children.Add(new ProgressBar { IsIndeterminate = true, Width = 60, Margin = new Thickness(10) });
        _content.Content = _scroll;
    }

    private static Control Placeholder(string text, string glyph)
    {
        var stack = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(20) };
        stack.Children.Add(Ui.Icon(glyph, 22, Ui.Secondary));
        stack.Children.Add(Ui.Text(text, brush: Ui.Secondary, wrap: true).With(t => t.TextAlignment = TextAlignment.Center));
        return stack;
    }
}

public sealed class ChatFileRow : Grid
{
    public ChatFileRow(AppModel model, ChatFile file)
    {
        ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto");
        Margin = new Thickness(12, 8);
        var thumbHost = new Grid { Width = 40, Height = 40, Margin = new Thickness(0, 0, 10, 0) };
        var thumb = new ContentControl { Content = new FileThumb(Ui.Load(file.Minithumbnail), file.FileName, blurred: true) };
        thumbHost.Children.Add(thumb);
        if (file.Kind is ChatFileKind.Video or ChatFileKind.Animation)
            thumbHost.Children.Add(Ui.Icon(Glyph.Play, 9, Brushes.White).With(i => { i.HorizontalAlignment = HorizontalAlignment.Right; i.VerticalAlignment = VerticalAlignment.Bottom; i.Margin = new Thickness(0, 0, 5, 5); }));
        Children.Add(thumbHost);
        if (file.ThumbnailFileId is not null)
        {
            _ = Task.Run(async () =>
            {
                var path = await model.ThumbnailPathAsync(file);
                Dispatcher.UIThread.Post(() => { if (Ui.Load(path) is { } image) thumb.Content = new FileThumb(image, file.FileName); });
            });
        }

        var name = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        name.Children.Add(Ui.Text(file.FileName).With(t => { t.TextTrimming = TextTrimming.PrefixCharacterEllipsis; t.MaxWidth = 190; }));
        if (model.IsFromTeleSave(file))
        {
            var badge = Ui.Icon(Glyph.Send, 9, Ui.Accent);
            ToolTip.SetTip(badge, L10n.T("Bu Mac'ten TeleSave ile yüklendi").Replace("Mac", "PC"));
            Avalonia.Automation.AutomationProperties.SetName(badge, L10n.T("TeleSave ile yüklendi"));
            name.Children.Add(badge);
        }
        var info = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(name);
        info.Children.Add(Ui.Caption($"{Format.Bytes(file.Size)} · {Format.UploadDate(file.Date)}"));
        ToolTip.SetTip(info, file.Caption.Length == 0 ? file.FileName : $"{file.FileName}\n{file.Caption}");
        Grid.SetColumn(info, 1);
        Children.Add(info);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (model.BusyChatFiles.Contains(file.Id)) actions.Children.Add(new ProgressBar { IsIndeterminate = true, Width = 20 });
        else
        {
            actions.Children.Add(Ui.IconButton(Glyph.Download, L10n.T("İndir (İndirilenler klasörüne)"), () => model.Download(file)));
            if (model.CanDelete(file)) actions.Children.Add(ConfirmDelete.Button(L10n.T("Telegram'dan sil"), () => model.Delete(file)));
        }
        Grid.SetColumn(actions, 2);
        Children.Add(actions);
    }
}

// MARK: - Login

public sealed class LoginPage : ModelPage
{
    private readonly Action _onSettings;
    private readonly TextBox _phone = new() { Watermark = "+90 5xx xxx xx xx" };
    private readonly TextBox _code = new() { Watermark = "12345", HorizontalContentAlignment = HorizontalAlignment.Center, FontSize = 18 };
    private readonly TextBox _password = new() { PasswordChar = '•' };
    private readonly ContentControl _body = new();
    private bool _usePhoneInsteadOfQr;
    private string _shownState = "";

    public LoginPage(AppModel model, Action onSettings) : base(model)
    {
        _onSettings = onSettings;
        _password.Watermark = L10n.T("Şifre");
        _phone.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) Model.SubmitPhone(_phone.Text ?? ""); };
        _code.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) Model.SubmitCode(_code.Text ?? ""); };
        _password.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) Model.SubmitPassword(_password.Text ?? ""); };
        Content = _body;
        Refresh();
    }

    protected override void Refresh()
    {
        var key = $"{Model.Auth}|{Model.AuthError}|{Model.IsBusy}|{_usePhoneInsteadOfQr}|{Model.NetworkAvailable}|{Model.AvailableUpdate}";
        if (key == _shownState) return;
        _shownState = key;
        var stack = new StackPanel { Spacing = 12, Margin = new Thickness(20), IsEnabled = !Model.IsBusy };
        var top = new Grid();
        top.Children.Add(Ui.IconButton(Glyph.Settings, L10n.T("Ayarlar"), _onSettings).With(b => b.HorizontalAlignment = HorizontalAlignment.Right));
        stack.Children.Add(top);
        foreach (var banner in StatusBanners.For(Model)) stack.Children.Add(banner);
        stack.Children.Add(Ui.Icon(Glyph.Send, 40, Ui.Accent));
        foreach (var control in StateControls()) stack.Children.Add(control);
        if (Model.AuthError is { } error)
            stack.Children.Add(Ui.Caption(error, Ui.Red, wrap: true).With(t => t.TextAlignment = TextAlignment.Center));
        _body.Content = new ScrollViewer { Content = stack };
    }

    private static TextBlock Title(string text) => Ui.Text(text, 17, FontWeight.Bold).With(t => t.HorizontalAlignment = HorizontalAlignment.Center);
    private static TextBlock Note(string text) => Ui.Caption(text, wrap: true).With(t => { t.TextAlignment = TextAlignment.Center; t.FontSize = 12.5; });

    private IEnumerable<Control> StateControls()
    {
        switch (Model.Auth)
        {
            case AuthState.Initializing or AuthState.Closing or AuthState.Closed or AuthState.LoggingOut:
                yield return new ProgressBar { IsIndeterminate = true, Width = 120 };
                yield return Note(L10n.T("Telegram'a bağlanılıyor…"));
                break;
            case AuthState.Ready:
                yield return new ProgressBar { IsIndeterminate = true, Width = 120 };
                yield return Note(L10n.T("Hesap yükleniyor…"));
                break;
            case AuthState.WaitPhoneNumber:
                yield return Title(L10n.T("Telegram ile giriş yapın"));
                yield return Note(L10n.T("Dosyalar kendi hesabınızın Kayıtlı Mesajlar sohbetine yüklenir."));
                yield return Ui.Primary(L10n.T("QR kod ile giriş"), Model.RequestQrCode);
                yield return Note(L10n.T("veya"));
                foreach (var c in PhoneForm()) yield return c;
                break;
            case AuthState.WaitQrConfirmation qr:
                if (_usePhoneInsteadOfQr)
                {
                    foreach (var c in PhoneForm()) yield return c;
                    yield return Ui.Link(L10n.T("QR koda dön"), () => { _usePhoneInsteadOfQr = false; Refresh(); });
                }
                else
                {
                    yield return Title(L10n.T("Telefonla tarayın"));
                    yield return QrImage(qr.Link);
                    yield return Note(L10n.T("Telegram › Ayarlar › Cihazlar › Masaüstü Cihaz Bağla"));
                    yield return Ui.Link(L10n.T("Telefon numarasıyla giriş"), () => { _usePhoneInsteadOfQr = true; Refresh(); });
                }
                break;
            case AuthState.WaitCode code:
                yield return Title(L10n.T("Doğrulama kodu"));
                yield return Note(code.Delivery switch
                {
                    AuthState.CodeDelivery.TelegramApp => L10n.T("Kod, %@ numaralı hesabın diğer Telegram oturumlarına gönderildi.", code.PhoneNumber),
                    AuthState.CodeDelivery.Sms => L10n.T("Kod %@ numarasına SMS ile gönderildi.", code.PhoneNumber),
                    AuthState.CodeDelivery.Call => L10n.T("Kod %@ numarasına arama ile iletilecek.", code.PhoneNumber),
                    _ => L10n.T("%@ için gönderilen kodu girin.", code.PhoneNumber)
                });
                yield return Detach(_code);
                yield return Ui.Primary(L10n.T("Doğrula"), () => Model.SubmitCode(_code.Text ?? ""));
                yield return Ui.Link(L10n.T("Baştan başla"), Model.LogOut, 11.5);
                break;
            case AuthState.WaitPassword password:
                yield return Title(L10n.T("İki adımlı doğrulama"));
                yield return Detach(_password);
                if (password.Hint.Length > 0) yield return Note(L10n.T("İpucu: %@", password.Hint));
                yield return Ui.Primary(L10n.T("Giriş yap"), () => Model.SubmitPassword(_password.Text ?? ""));
                yield return Ui.Link(L10n.T("Baştan başla"), Model.LogOut, 11.5);
                break;
            case AuthState.WaitRegistration:
                yield return Title(L10n.T("Bu numarada Telegram hesabı yok."));
                yield return Note(L10n.T("Önce telefondaki Telegram uygulamasından kayıt olun, sonra tekrar deneyin."));
                yield return Ui.Link(L10n.T("Baştan başla"), Model.LogOut, 11.5);
                break;
        }
    }

    private IEnumerable<Control> PhoneForm()
    {
        yield return Detach(_phone);
        yield return new Button { Content = L10n.T("Kod gönder"), HorizontalAlignment = HorizontalAlignment.Center }
            .With(b => b.Click += (_, _) => Model.SubmitPhone(_phone.Text ?? ""));
    }

    /// <summary>Re-parent a persistent input (it keeps its text across redraws).</summary>
    private static Control Detach(Control control)
    {
        if (control.Parent is Panel panel) panel.Children.Remove(control);
        return control;
    }

    private static Control QrImage(string link)
    {
        if (link.Length == 0) return new ProgressBar { IsIndeterminate = true, Width = 80 };
        using var generator = new QRCodeGenerator();
        var png = new PngByteQRCode(generator.CreateQrCode(link, QRCodeGenerator.ECCLevel.M)).GetGraphic(8);
        return new Border
        {
            Background = Brushes.White, Padding = new Thickness(8), CornerRadius = new CornerRadius(8), Width = 180, Height = 180,
            HorizontalAlignment = HorizontalAlignment.Center, Child = new Image { Source = Ui.Load(png) }
        };
    }
}

// MARK: - API key

/// <summary>API key entry: first-run setup, or (with onClose) changing the key from Settings.</summary>
public sealed class CredentialsPage : ModelPage
{
    private readonly Action? _onClose;
    private readonly TextBox _apiId = new() { Watermark = L10n.T("api_id (ör. 1234567)") };
    private readonly TextBox _apiHash = new() { Watermark = L10n.T("api_hash (32 karakter)"), FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace") };
    private readonly TextBlock _error = Ui.Caption("", Ui.Red, wrap: true);
    private readonly StackPanel _validating = new() { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _save;

    public CredentialsPage(AppModel model, Action? onClose) : base(model)
    {
        _onClose = onClose;
        model.AuthError = null;
        var isChange = onClose is not null;
        var stack = new StackPanel { Spacing = 10, Margin = new Thickness(20) };
        stack.Children.Add(Ui.Text(isChange ? L10n.T("API anahtarını değiştir") : L10n.T("Telegram API anahtarı"), 17, FontWeight.Bold));
        if (isChange)
        {
            if (model.CurrentApiId is { } current) stack.Children.Add(Ui.Caption(L10n.T("Mevcut api_id: %@", current.ToString())));
            stack.Children.Add(Ui.Text(L10n.T("Yeni anahtar doğrulanana kadar mevcut anahtar kullanılmaya devam eder."), 12.5, wrap: true));
        }
        else
        {
            stack.Children.Add(Ui.Text(L10n.T("TeleSave'in hesabınıza bağlanması için bir Telegram API anahtarı gerekiyor (tek seferlik):"), 12.5, wrap: true));
        }
        var steps = new StackPanel { Spacing = 2 };
        steps.Children.Add(Ui.Caption(L10n.T("1. my.telegram.org adresinde telefonunuzla giriş yapın."), wrap: true));
        steps.Children.Add(Ui.Caption(L10n.T("2. “API development tools” › yeni uygulama oluşturun."), wrap: true));
        steps.Children.Add(Ui.Caption(L10n.T("3. api_id ve api_hash değerlerini aşağıya yapıştırın."), wrap: true));
        stack.Children.Add(steps);
        stack.Children.Add(Ui.Link(L10n.T("my.telegram.org'u aç"), () => model.Platform.OpenUrl(new Uri("https://my.telegram.org/apps")), 12.5)
            .With(l => l.HorizontalAlignment = HorizontalAlignment.Left));
        stack.Children.Add(_apiId);
        stack.Children.Add(_apiHash);
        stack.Children.Add(_error);

        _validating.Children.Add(new ProgressBar { IsIndeterminate = true, Width = 40 });
        _validating.Children.Add(Ui.Caption(L10n.T("Doğrulanıyor…")));
        _save = new Button { Content = L10n.T("Doğrula ve kaydet") };
        _save.Classes.Add("accent");
        _save.Click += async (_, _) =>
        {
            if (await Model.SaveCredentialsAsync(_apiId.Text ?? "", _apiHash.Text ?? "")) _onClose?.Invoke();
        };
        var cancel = new Button { Content = isChange ? L10n.T("Vazgeç") : L10n.T("Çıkış") };
        cancel.Click += (_, _) =>
        {
            Model.AuthError = null;
            if (_onClose is not null) _onClose();
            else if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
        };
        var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto") };
        buttons.Children.Add(cancel);
        Grid.SetColumn(_validating, 2);
        _validating.Margin = new Thickness(0, 0, 8, 0);
        buttons.Children.Add(_validating);
        Grid.SetColumn(_save, 3);
        buttons.Children.Add(_save);
        stack.Children.Add(buttons);
        stack.Children.Add(Ui.Caption(L10n.T("Anahtar yalnızca bu Mac'in Keychain'inde saklanır.").Replace("Mac'in Keychain'inde", "bilgisayarda, şifreli olarak"), wrap: true));
        Content = new ScrollViewer { Content = stack };
        _apiId.TextChanged += (_, _) => Refresh();
        _apiHash.TextChanged += (_, _) => Refresh();
        Refresh();
    }

    protected override void Refresh()
    {
        _error.Text = Model.AuthError ?? "";
        _error.IsVisible = Model.AuthError is not null;
        _validating.IsVisible = Model.IsValidatingKey;
        _save.IsEnabled = !Model.IsValidatingKey && !string.IsNullOrWhiteSpace(_apiId.Text) && !string.IsNullOrWhiteSpace(_apiHash.Text);
    }
}

// MARK: - Settings

public sealed class SettingsPage : ModelPage
{
    private readonly ContentControl _body = new();
    private readonly Action _onClose;
    private readonly Action _onChangeKey;
    private string _shown = "";

    public SettingsPage(AppModel model, Action onClose, Action onChangeKey) : base(model)
    {
        _onClose = onClose;
        _onChangeKey = onChangeKey;
        var layout = new DockPanel();
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(12, 8) };
        header.Children.Add(Ui.Link("‹ " + L10n.T("Geri"), onClose, 13));
        header.Children.Add(Ui.Text(L10n.T("Ayarlar"), 14, FontWeight.SemiBold).With(t => { Grid.SetColumn(t, 1); t.HorizontalAlignment = HorizontalAlignment.Center; }));
        var top = new StackPanel();
        top.Children.Add(header);
        top.Children.Add(Ui.Divider());
        DockPanel.SetDock(top, Dock.Top);
        layout.Children.Add(top);
        layout.Children.Add(_body);
        Content = layout;
        Refresh();
    }

    protected override void Refresh()
    {
        var key = $"{Model.Settings}|{Model.Me}|{Model.Auth}|{Model.AvailableUpdate}|{string.Join(",", Model.FailedShortcuts)}";
        if (key == _shown) return;
        _shown = key;
        var s = Model.Settings;
        var stack = new StackPanel { Spacing = 14, Margin = new Thickness(16, 12) };

        stack.Children.Add(Section(L10n.T("Gönderim"),
            Toggle(L10n.T("Fotoğraf/videoları medya olarak gönder"), s.SendMediaCompressed, v => s with { SendMediaCompressed = v },
                L10n.T("Kapalıyken her şey belge olarak, orijinal kalitede gönderilir.")),
            Toggle(L10n.T("Birlikte bırakılanları albüm yap (en fazla 10)"), s.GroupAsAlbum, v => s with { GroupAsAlbum = v }),
            Choice(L10n.T("Klasörler"), [L10n.T("Zip olarak"), L10n.T("İçindeki dosyalar ayrı ayrı")], s.FolderMode == FolderMode.Expand ? 1 : 0,
                i => s with { FolderMode = i == 1 ? FolderMode.Expand : FolderMode.Zip }),
            Choice(L10n.T("Aynı anda yükleme: %@", s.MaxConcurrent.ToString()), ["1", "2", "3", "4"], Math.Clamp(s.MaxConcurrent, 1, 4) - 1,
                i => s with { MaxConcurrent = i + 1 }),
            Caption()));

        stack.Children.Add(Section(L10n.T("Kısayollar"), ShortcutRows(s)));

        var languages = new List<string> { L10n.T("Sistem (%@)", L10n.Languages.FirstOrDefault(l => l.Code == L10n.SystemLanguageCode)?.Name ?? "English") };
        languages.AddRange(L10n.Languages.Select(l => l.Name));
        var languageIndex = s.Language == L10n.SystemCode ? 0 : 1 + L10n.Languages.ToList().FindIndex(l => l.Code == s.Language);
        stack.Children.Add(Section(L10n.T("Uygulama"),
            Choice(L10n.T("Dil"), languages, Math.Max(0, languageIndex), i => s with { Language = i == 0 ? L10n.SystemCode : L10n.Languages[i - 1].Code }),
            AutostartToggle(),
            Toggle(L10n.T("Görev çubuğunda göster"), s.ShowInTaskbar, v => s with { ShowInTaskbar = v },
                L10n.T("Dosyayı görev çubuğundaki TeleSave düğmesinin üstüne sürükleyince bırakma paneli açılır.")),
            Toggle(L10n.T("Sağ tık menüsünde “%@”", L10n.T("TeleSave'e gönder")), s.ContextMenuEnabled, v => s with { ContextMenuEnabled = v },
                L10n.T("Gezgin'de dosya ve klasörlere sağ tıklayınca görünür.")),
            Toggle(L10n.T("Ekran kenarında bırakma şeridi"), s.EdgeShelf, v => s with { EdgeShelf = v },
                L10n.T("Ekranın sağ kenarında ince bir şerit; üstüne dosya sürükleyince genişler.")),
            Toggle(L10n.T("Sohbetteki tüm dosyaları göster"), s.ShowChatFiles, v => s with { ShowChatFiles = v },
                L10n.T("Açıkken listede seçili sohbetin tüm dosyaları görünür; kapalıyken sadece TeleSave ile yüklenenler.")),
            Toggle(L10n.T("Bildirim göster"), s.NotificationsEnabled, v => s with { NotificationsEnabled = v }),
            Toggle(L10n.T("İkona bırakınca pencereyi aç"), s.OpenPanelOnDrop, v => s with { OpenPanelOnDrop = v })));

        var account = new List<Control>();
        if (Model.Me is { } me)
        {
            account.Add(Labeled(L10n.T("Hesap"), me.DisplayName));
            if (me.Username is { } u) account.Add(Labeled(L10n.T("Kullanıcı adı"), "@" + u));
            account.Add(Labeled(L10n.T("Dosya sınırı"), Format.Bytes(me.MaxUploadSize)));
        }
        if (Model.Auth.IsReady) account.Add(new Button { Content = L10n.T("Telegram oturumunu kapat"), Foreground = Ui.Red }.With(b => b.Click += (_, _) => Model.LogOut()));
        if (!Model.HasBundledCredentials) account.Add(new Button { Content = L10n.T("API anahtarını değiştir") }.With(b => b.Click += (_, _) => _onChangeKey()));
        stack.Children.Add(Section(L10n.T("Hesap"), account.ToArray()));
        stack.Children.Add(AboutRow());
        _body.Content = new ScrollViewer { Content = stack };
    }

    private void Update(Func<AppSettings, AppSettings> change) => Model.UpdateSettings(_ => change(Model.Settings));

    private Control Section(string title, params Control[] rows)
    {
        var card = new StackPanel { Spacing = 10 };
        foreach (var row in rows) card.Children.Add(row);
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(Ui.Text(title, 12, FontWeight.SemiBold, Ui.Secondary));
        panel.Children.Add(new Border { Child = card, Padding = new Thickness(12), CornerRadius = new CornerRadius(8), Background = new SolidColorBrush(Colors.Gray, 0.08) });
        return panel;
    }

    private Control Toggle(string label, bool value, Func<bool, AppSettings> apply, string? help = null)
    {
        var toggle = new ToggleSwitch { IsChecked = value, OnContent = null, OffContent = null, MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Right };
        toggle.IsCheckedChanged += (_, _) => Update(_ => apply(toggle.IsChecked == true));
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var text = Ui.Text(label, 12.5, wrap: true).With(t => t.VerticalAlignment = VerticalAlignment.Center);
        row.Children.Add(text);
        Grid.SetColumn(toggle, 1);
        row.Children.Add(toggle);
        if (help is not null) ToolTip.SetTip(row, help);
        return row;
    }

    private Control Choice(string label, IReadOnlyList<string> options, int selected, Func<int, AppSettings> apply)
    {
        var combo = new ComboBox { ItemsSource = options, SelectedIndex = selected, MinWidth = 120, HorizontalAlignment = HorizontalAlignment.Right };
        combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0 && combo.SelectedIndex != selected) Update(_ => apply(combo.SelectedIndex)); };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(Ui.Text(label, 12.5, wrap: true).With(t => t.VerticalAlignment = VerticalAlignment.Center));
        Grid.SetColumn(combo, 1);
        row.Children.Add(combo);
        return row;
    }

    private Control[] ShortcutRows(AppSettings s)
    {
        if (!Model.Platform.Shortcuts.IsSupported)
        {
            // Wayland: apps cannot register global shortcuts; the desktop's own shortcut settings can run the command.
            return [Ui.Caption(L10n.T("Bu masaüstünde uygulamalar genel kısayol kaydedemez. Sistem ayarlarındaki klavye kısayollarından şu komuta bir kısayol atayın: %@", "telesave --send-clipboard"), wrap: true)];
        }
        return
        [
            ShortcutRow(L10n.T("Kopyalananları yükle"), s.UploadShortcut, ShortcutAction.UploadClipboard, v => Model.Settings with { UploadShortcut = v },
                L10n.T("Dosya yöneticisinde Ctrl+C ile kopyaladığınız dosyaları ya da bir ekran görüntüsünü her yerden gönderir.")),
            ShortcutRow(L10n.T("Paneli aç/kapat"), s.PanelShortcut, ShortcutAction.TogglePanel, v => Model.Settings with { PanelShortcut = v }),
        ];
    }

    /// <summary>Click the button, then press the new combination. Esc cancels, Backspace/Delete turns the shortcut off.</summary>
    private Control ShortcutRow(string label, KeyShortcut? value, ShortcutAction action, Func<KeyShortcut?, AppSettings> apply, string? help = null)
    {
        var windows = OperatingSystem.IsWindows();
        string Shown() => value?.Display(windows) ?? L10n.T("Yok");
        var button = new Button { Content = Shown(), MinWidth = 140, HorizontalContentAlignment = HorizontalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        ToolTip.SetTip(button, L10n.T("Değiştirmek için tıklayın, sonra yeni tuş kombinasyonuna basın. Esc: vazgeç, Backspace: kapat."));
        var warning = Ui.Caption(Model.ShortcutConflict(action) ?? "", Ui.Orange, wrap: true);
        warning.IsVisible = warning.Text!.Length > 0;

        TopLevel? top = null;
        EventHandler<KeyEventArgs>? handler = null;
        void Stop()
        {
            if (handler is null) return;
            top?.RemoveHandler(KeyDownEvent, handler);
            handler = null;
            Model.Platform.Shortcuts.Suspend(false);
        }
        void Set(KeyShortcut? shortcut)
        {
            Stop();
            button.Content = shortcut?.Display(windows) ?? L10n.T("Yok");
            Update(_ => apply(shortcut));
        }
        button.Click += (_, _) =>
        {
            if (handler is not null) { Stop(); button.Content = Shown(); return; }
            top = TopLevel.GetTopLevel(button);
            if (top is null) return;
            button.Content = L10n.T("Tuşlara basın…");
            Model.Platform.Shortcuts.Suspend(true);
            handler = (_, e) =>
            {
                e.Handled = true;
                if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
                var plain = e.KeyModifiers == KeyModifiers.None;
                if (plain && e.Key == Key.Escape) { Stop(); button.Content = Shown(); }
                else if (plain && e.Key is Key.Back or Key.Delete) Set(null);
                else if (KeyShortcut.From(e.Key, e.KeyModifiers) is { } shortcut) Set(shortcut);
                else
                {
                    warning.Text = L10n.T("Ctrl, Alt veya %@ ile birlikte bir harf, rakam ya da F tuşuna basın.", windows ? "Win" : "Super");
                    warning.IsVisible = true;
                }
            };
            top.AddHandler(KeyDownEvent, handler, RoutingStrategies.Tunnel);
        };
        button.LostFocus += (_, _) => { if (handler is not null) { Stop(); button.Content = Shown(); } };

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Ui.Text(label, 12.5, wrap: true));
        if (help is not null) text.Children.Add(Ui.Caption(help, wrap: true));
        row.Children.Add(text);
        Grid.SetColumn(button, 1);
        row.Children.Add(button);
        var stack = new StackPanel { Spacing = 3 };
        stack.Children.Add(row);
        stack.Children.Add(warning);
        return stack;
    }

    private Control Caption()
    {
        var box = new TextBox { Text = Model.Settings.CaptionTemplate, Watermark = L10n.T("ör. #telesave {filename}") };
        box.LostFocus += (_, _) => Update(s => s with { CaptionTemplate = box.Text ?? "" });
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(Ui.Text(L10n.T("Açıklama şablonu"), 12.5));
        stack.Children.Add(box);
        stack.Children.Add(Ui.Caption(L10n.T("Değişkenler: {filename} {date} {size}")));
        return stack;
    }

    private Control AutostartToggle()
    {
        var toggle = new ToggleSwitch { IsChecked = Model.Platform.IsAutostartEnabled, OnContent = null, OffContent = null, MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Right };
        toggle.IsCheckedChanged += (_, _) =>
        {
            try { Model.Platform.SetAutostart(toggle.IsChecked == true); } catch (Exception e) { Model.ShowNotice(e.Message); }
        };
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(Ui.Text(L10n.T("Girişte başlat"), 12.5).With(t => t.VerticalAlignment = VerticalAlignment.Center));
        Grid.SetColumn(toggle, 1);
        row.Children.Add(toggle);
        return row;
    }

    private static Control Labeled(string label, string value)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(Ui.Text(label, 12.5));
        row.Children.Add(Ui.Text(value, 12.5, brush: Ui.Secondary).With(t => Grid.SetColumn(t, 1)));
        return row;
    }

    /// <summary>[GitHub] TeleSave &lt;version&gt; [update] ↗ — opens the new release if there is one, else the repo.</summary>
    private Control AboutRow()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(GitHubMark.Create(16));
        row.Children.Add(Ui.Text("TeleSave", 13));
        row.Children.Add(Ui.Text(Model.AppVersion, 13, brush: Ui.Secondary));
        if (Model.AvailableUpdate is { } update)
        {
            row.Children.Add(new Border
            {
                Background = Ui.Accent, CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 1),
                Child = Ui.Text(L10n.T("Güncelleme var: %@", update.Version), 11, FontWeight.Medium, Brushes.White)
            });
        }
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(row);
        grid.Children.Add(Ui.Icon(Glyph.OpenExternal, 11, Ui.Secondary).With(i => Grid.SetColumn(i, 1)));
        var button = new Button
        {
            Content = grid, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Colors.Gray, 0.08), Padding = new Thickness(12, 10), CornerRadius = new CornerRadius(8)
        };
        ToolTip.SetTip(button, Model.AvailableUpdate is null ? $"github.com/{UpdateChecker.Repository}" : L10n.T("Yeni sürümü indir"));
        button.Click += (_, _) => Model.Platform.OpenUrl(Model.AvailableUpdate?.PageUrl ?? UpdateChecker.RepositoryUrl);
        return button;
    }
}

/// <summary>GitHub mark (Octicons mark-github) as a vector path, tinted with the text color.</summary>
internal static class GitHubMark
{
    private const string PathData = "M8 0c4.42 0 8 3.58 8 8a8.013 8.013 0 0 1-5.45 7.59c-.4.08-.55-.17-.55-.38 0-.27.01-1.13.01-2.2 0-.75-.25-1.23-.54-1.48 1.78-.2 3.65-.88 3.65-3.95 0-.88-.31-1.59-.82-2.15.08-.2.36-1.02-.08-2.12 0 0-.67-.22-2.2.82-.64-.18-1.32-.27-2-.27-.68 0-1.36.09-2 .27-1.53-1.03-2.2-.82-2.2-.82-.44 1.1-.16 1.92-.08 2.12-.51.56-.82 1.28-.82 2.15 0 3.06 1.86 3.75 3.64 3.95-.23.2-.44.55-.51 1.07-.46.21-1.61.55-2.33-.66-.15-.24-.6-.83-1.23-.82-.67.01-.27.38.01.53.34.19.73.9.82 1.13.16.45.68 1.31 2.69.94 0 .67.01 1.3.01 1.49 0 .21-.15.45-.55.38A7.995 7.995 0 0 1 0 8c0-4.42 3.58-8 8-8Z";

    public static Control Create(double size)
    {
        var path = new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(PathData), Stretch = Stretch.Uniform, Width = size, Height = size };
        path.Bind(Avalonia.Controls.Shapes.Shape.FillProperty, path.GetResourceObservable("TextControlForeground"));
        return path;
    }
}

// MARK: - Destination picker

public sealed class DestinationPage : ModelPage
{
    private readonly Action _onClose;
    private readonly TextBox _search = new() { Watermark = L10n.T("Sohbet ara") };
    private readonly StackPanel _list = new();
    private readonly ContentControl _error = new() { Margin = new Thickness(12, 0, 12, 8) };
    private readonly TextBlock _title;
    private ChatSummary? _forumChat;
    private IReadOnlyList<ForumTopicSummary> _topics = [];
    private long? _checking;

    public DestinationPage(AppModel model, Action onClose) : base(model)
    {
        _onClose = onClose;
        model.DestinationError = null;
        _title = Ui.Text(L10n.T("Yükleme hedefi"), 14, FontWeight.SemiBold);
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(12, 8) };
        header.Children.Add(Ui.Link("‹ " + L10n.T("Geri"), Back, 13));
        _title.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetColumn(_title, 1);
        header.Children.Add(_title);
        var refresh = Ui.IconButton(Glyph.Refresh, L10n.T("Sohbetleri yenile"), () => _ = Model.RefreshChatsAsync());
        Grid.SetColumn(refresh, 2);
        header.Children.Add(refresh);
        var top = new StackPanel();
        top.Children.Add(header);
        top.Children.Add(Ui.Divider());
        top.Children.Add(_search.With(s => s.Margin = new Thickness(12, 10, 12, 6)));
        top.Children.Add(_error);
        var layout = new DockPanel();
        DockPanel.SetDock(top, Dock.Top);
        layout.Children.Add(top);
        layout.Children.Add(new ScrollViewer { Content = _list });
        Content = layout;
        _search.TextChanged += (_, _) => Refresh();
        AttachedToVisualTree += (_, _) => { if (Model.Chats.Count == 0) _ = Model.RefreshChatsAsync(); };
        Refresh();
    }

    private void Back()
    {
        if (_forumChat is not null) { _forumChat = null; Refresh(); }
        else _onClose();
    }

    protected override void Refresh()
    {
        _title.Text = _forumChat?.Title ?? L10n.T("Yükleme hedefi");
        _search.IsVisible = _forumChat is null;
        _error.Content = Model.DestinationError is { } error ? Ui.Banner(error, Glyph.Warning, Ui.Orange, () => Model.DestinationError = null) : null;
        _list.Children.Clear();
        if (_forumChat is { } forum)
        {
            if (_topics.Count == 0) _list.Children.Add(new ProgressBar { IsIndeterminate = true, Width = 60, Margin = new Thickness(10) });
            foreach (var topic in _topics)
            {
                var selected = Model.Destination.ChatId == forum.Id && Model.Destination.ForumTopicId == topic.Id;
                // The General topic is the chat itself; no topic id needed.
                _list.Children.Add(Row(topic.IsGeneral ? Glyph.Number : Glyph.Chat, topic.Name, null, selected, false,
                    async () => { if (await Model.ChooseAsync(forum, topic.IsGeneral ? null : topic)) _onClose(); }));
            }
            return;
        }
        _list.Children.Add(Row(Glyph.Bookmark, L10n.T("Saved Messages"), L10n.T("Kendi bulut sohbetiniz"), Model.Destination.IsSavedMessages, false,
            () => { Model.Select(UploadDestination.SavedMessages); _onClose(); return Task.CompletedTask; }));
        var query = (_search.Text ?? "").Trim();
        if (query.Length == 0 && Model.RecentDestinations.Count > 0)
        {
            _list.Children.Add(SectionTitle(L10n.T("Son kullanılanlar")));
            foreach (var recent in Model.RecentDestinations)
                _list.Children.Add(Row(Glyph.Clock, recent.Title, null, recent == Model.Destination, false,
                    () => { Model.Select(recent); _onClose(); return Task.CompletedTask; }));
        }
        _list.Children.Add(SectionTitle(L10n.T("Sohbetler")));
        var chats = Model.Chats.Where(c => query.Length == 0 || SearchText.Matches(c.Title + " " + c.Username, query)).ToList();
        if (Model.IsLoadingChats && Model.Chats.Count == 0) _list.Children.Add(new ProgressBar { IsIndeterminate = true, Width = 60, Margin = new Thickness(10) });
        else if (chats.Count == 0) _list.Children.Add(Ui.Caption(L10n.T("Sohbet bulunamadı")).With(t => t.Margin = new Thickness(16, 6)));
        foreach (var chat in chats)
        {
            var selected = Model.Destination.ChatId == chat.Id && Model.Destination.ForumTopicId is null;
            _list.Children.Add(Row(IconFor(chat), chat.Title, SubtitleFor(chat), selected, _checking == chat.Id, () => OpenAsync(chat)));
        }
    }

    private async Task OpenAsync(ChatSummary chat)
    {
        if (chat.IsForum)
        {
            _forumChat = chat;
            _topics = [];
            Refresh();
            _topics = await Model.ForumTopicsAsync(chat);
            Refresh();
            return;
        }
        _checking = chat.Id;
        Refresh();
        var ok = await Model.ChooseAsync(chat);
        _checking = null;
        if (ok) _onClose(); else Refresh();
    }

    private static Control SectionTitle(string text) => Ui.Text(text, 11.5, FontWeight.SemiBold, Ui.Secondary).With(t => t.Margin = new Thickness(16, 12, 16, 4));

    private static Control Row(string glyph, string title, string? subtitle, bool selected, bool busy, Func<Task> action)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
        grid.Children.Add(Ui.Icon(glyph, 15, Ui.Accent).With(i => { i.Width = 24; i.Margin = new Thickness(0, 0, 10, 0); }));
        var texts = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(Ui.Text(title, 13));
        if (subtitle is not null) texts.Children.Add(Ui.Caption(subtitle));
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        if (busy || selected)
        {
            Control mark = busy ? new ProgressBar { IsIndeterminate = true, Width = 20 } : Ui.Icon(Glyph.Check, 12, Ui.Accent);
            Grid.SetColumn(mark, 2);
            grid.Children.Add(mark);
        }
        var button = new Button
        {
            Content = grid, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(16, 7)
        };
        button.Click += async (_, _) => await action();
        return button;
    }

    private static string IconFor(ChatSummary chat) => chat.Kind switch
    {
        ChatKind.User => Glyph.Person,
        ChatKind.Bot => Glyph.Bot,
        ChatKind.Group => chat.IsForum ? Glyph.Forum : Glyph.People,
        ChatKind.Channel => Glyph.Megaphone,
        _ => Glyph.Bookmark
    };

    private static string SubtitleFor(ChatSummary chat)
    {
        var kind = chat.Kind switch
        {
            ChatKind.User => L10n.T("Kişi"),
            ChatKind.Bot => L10n.T("Bot"),
            ChatKind.Group => chat.IsForum ? L10n.T("Forum grubu") : L10n.T("Grup"),
            ChatKind.Channel => L10n.T("Kanal"),
            _ => L10n.T("Saved Messages")
        };
        return chat.Username is { } u ? $"{kind} · @{u}" : kind;
    }
}
