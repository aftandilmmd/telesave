using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using TeleSave.Core;
using TeleSave.UI;

namespace TeleSave.Shell;

public sealed class ShellApp : Application
{
    public static Func<IShellPlatform> CreatePlatform { get; set; } = () => throw new InvalidOperationException("No platform");
    public static IReadOnlyList<string> InitialPaths { get; set; } = [];
    public static bool LaunchedAtLogin { get; set; }
    public static string PipeName { get; set; } = "TeleSave";

    private AppModel? _model;
    private PanelWindow? _panel;
    private EdgeShelfWindow? _shelf;
    private TrayIcon? _tray;
    private readonly List<string> _incoming = [];
    private DispatcherTimer? _incomingTimer;

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Default;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var platform = CreatePlatform();
            _model = new AppModel(platform, action => Dispatcher.UIThread.Post(action));
            _panel = new PanelWindow(_model, platform);
            _model.PanelRequested += () => _panel.ShowPanel();
            _model.PanelToggleRequested += () => _panel.TogglePanel();
            _model.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(AppModel.StatusIcon) or nameof(AppModel.Settings) or nameof(AppModel.Auth)) UpdateTray();
                if (e.PropertyName == nameof(AppModel.Settings)) ApplyWindowSettings();
            };
            _model.ApplyContextMenu();
            _model.EnableAutostartOnFirstLaunch();
            _model.StartShortcuts();
            CreateTray();
            ApplyWindowSettings();
            StartPipeServer();

            // First run, setup needed, or files passed in: show the panel. At login, start quietly.
            if (InitialPaths.Count > 0) Receive(InitialPaths);
            else if (!LaunchedAtLogin || _model.Phase == AppModel.AppPhase.NeedsCredentials) _panel.ShowPanel();

            desktop.Exit += (_, _) =>
            {
                _model.Shutdown();
                _tray?.Dispose();
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void ApplyWindowSettings()
    {
        if (_model is null || _panel is null) return;
        _panel.ApplyTaskbarMode(_model.Settings.ShowInTaskbar);
        if (_model.Settings.EdgeShelf)
        {
            _shelf ??= new EdgeShelfWindow(_model);
            _shelf.Show();
        }
        else
        {
            _shelf?.Hide();
        }
    }

    // MARK: Tray

    private void CreateTray()
    {
        _tray = new TrayIcon { IsVisible = true };
        _tray.Clicked += (_, _) => _panel?.TogglePanel();
        TrayIcon.SetIcons(this, [_tray]);
        UpdateTray();
    }

    private void UpdateTray()
    {
        if (_tray is null || _model is null) return;
        _tray.Icon = TrayIconRenderer.Render(_model.StatusIcon);
        _tray.ToolTipText = _model.StatusIcon is AppModel.StatusIconState.Uploading uploading
            ? $"TeleSave · {Format.Percent(uploading.Progress)}"
            : L10n.T("TeleSave — dosyaları buraya bırakın");
        var menu = new NativeMenu();
        NativeMenuItem Item(string title, Action action, bool enabled = true)
        {
            var item = new NativeMenuItem(title) { IsEnabled = enabled };
            item.Click += (_, _) => action();
            return item;
        }
        menu.Add(Item(L10n.T("Dosya seç…"), () => _panel?.PickFiles(), _model.CanUpload));
        menu.Add(Item(L10n.T("Panodan yapıştır"), () => _model.PasteFromClipboard(), _model.CanUpload));
        menu.Add(Item(L10n.T("Kayıtlı Mesajları aç"), () => _model.OpenInTelegram(), _model.CanUpload));
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Item(L10n.T("TeleSave'i aç"), () => _panel?.ShowPanel()));
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(Item(L10n.T("Çıkış"), () => (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown()));
        _tray.Menu = menu;
    }

    // MARK: Files from other processes (Explorer menu, Send to)

    private void StartPipeServer()
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte);
                    server.WaitForConnection();
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var paths = JsonSerializer.Deserialize<List<string>>(reader.ReadToEnd()) ?? [];
                    Dispatcher.UIThread.Post(() => Receive(paths));
                }
                catch (Exception) { Thread.Sleep(200); }
            }
        }) { IsBackground = true, Name = "TeleSave.Pipe" };
        thread.Start();
    }

    /// <summary>
    /// Explorer starts one process per selected file for menu verbs; collect what arrives within
    /// 300 ms so a multi-selection becomes one batch (one album, one notification).
    /// </summary>
    private void Receive(IReadOnlyList<string> paths)
    {
        if (paths is [ShellHost.SendClipboardArgument])
        {
            _model?.UploadClipboardFromShortcut();
            return;
        }
        if (paths.Count == 0) { _panel?.ShowPanel(); return; }
        _incoming.AddRange(paths);
        _incomingTimer?.Stop();
        _incomingTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(300), DispatcherPriority.Normal, (_, _) =>
        {
            _incomingTimer?.Stop();
            var batch = _incoming.Distinct().ToList();
            _incoming.Clear();
            _model?.HandleDrop(batch, fromShell: true);
        });
        _incomingTimer.Start();
    }
}
