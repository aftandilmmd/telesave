using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using TeleSave.Core;
using TeleSave.UI;
using TeleSave.UI.Views;

namespace TeleSave.Shell;

/// <summary>
/// The drop panel. Opens next to the tray icon (or wherever the taskbar is). With "Show in taskbar" it
/// keeps a taskbar button: when it loses focus it minimizes instead of hiding, so dragging a file onto
/// the button brings it back (standard Windows behavior) and the file can be dropped anywhere on it.
/// </summary>
public sealed class PanelWindow : Window
{
    private readonly AppModel _model;
    private readonly IShellPlatform _platform;
    private readonly PanelView _view;
    private bool _taskbarMode = true;
    private bool _positioned;

    public PanelWindow(AppModel model, IShellPlatform platform)
    {
        _model = model;
        _platform = platform;
        _view = new PanelView(model);
        Content = _view;
        Title = "TeleSave";
        Width = 360;
        Height = 480;
        CanResize = false;
        SystemDecorations = SystemDecorations.BorderOnly;
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaTitleBarHeightHint = -1;
        Icon = new WindowIcon(AssetLoader());
        WindowStartupLocation = WindowStartupLocation.Manual;

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None);
        AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (e.Handled) return;
            var paths = e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList() ?? [];
            _model.HandleDrop(paths);
            e.Handled = true;
        });
        KeyDown += (_, e) =>
        {
            // Ctrl+V pastes files/images unless a text box is being edited.
            if (e.Key == Key.V && e.KeyModifiers == KeyModifiers.Control && FocusManager?.GetFocusedElement() is not TextBox && _model.CanUpload)
            {
                _model.PasteFromClipboard();
                e.Handled = true;
            }
            if (e.Key == Key.Escape) HidePanel();
        };
        Deactivated += (_, _) => HidePanel();
        Closing += (_, e) =>
        {
            // The window lives as long as the app; closing it just hides the panel.
            e.Cancel = true;
            HidePanel();
        };
    }

    private static Stream AssetLoader() => Avalonia.Platform.AssetLoader.Open(new Uri("avares://TeleSave.Shell/icon.png"));

    public void ApplyTaskbarMode(bool taskbar)
    {
        _taskbarMode = taskbar;
        ShowInTaskbar = taskbar;
        if (taskbar && !IsVisible)
        {
            // Keep a (minimized) window so the taskbar button exists for drag-and-drop.
            PositionNearTray();
            Show();
            WindowState = WindowState.Minimized;
        }
        else if (!taskbar && WindowState == WindowState.Minimized)
        {
            Hide();
            WindowState = WindowState.Normal;
        }
    }

    public void TogglePanel()
    {
        if (IsVisible && WindowState == WindowState.Normal && IsActive) HidePanel();
        else ShowPanel();
    }

    public void ShowPanel()
    {
        if (!_positioned || WindowState == WindowState.Minimized || !IsVisible) PositionNearTray();
        Show();
        WindowState = WindowState.Normal;
        Topmost = true;
        Activate();
    }

    public void HidePanel()
    {
        Topmost = false;
        if (_taskbarMode) WindowState = WindowState.Minimized;
        else Hide();
    }

    public async void PickFiles()
    {
        ShowPanel();
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = true, Title = L10n.T("Kayıtlı Mesajlara yüklenecek dosyaları seçin") });
        _model.HandleDrop(files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList());
    }

    /// <summary>
    /// Places the panel next to the panel/taskbar on the screen the pointer is on: near the pointer when
    /// the OS tells us where it is (the tray icon was just clicked), otherwise in the corner by the clock.
    /// </summary>
    private void PositionNearTray()
    {
        var pointer = _platform.PointerPosition;
        var screen = (pointer is { } p ? Screens.ScreenFromPoint(p) : null) ?? Screens.Primary;
        if (screen is null) return;
        var cursor = pointer ?? new PixelPoint(screen.WorkingArea.Right, screen.WorkingArea.Bottom);
        var area = screen.WorkingArea;
        var scale = screen.Scaling;
        var width = (int)(Width * scale);
        var height = (int)(Height * scale);
        const int margin = 12;
        var taskbarBottom = screen.Bounds.Bottom > area.Bottom;
        var taskbarTop = area.Y > screen.Bounds.Y;
        var taskbarLeft = area.X > screen.Bounds.X;
        var x = Math.Clamp(cursor.X - width / 2, area.X + margin, area.Right - width - margin);
        var y = taskbarTop ? area.Y + margin : area.Bottom - height - margin;
        if (taskbarLeft) x = area.X + margin;
        else if (!taskbarBottom && !taskbarTop) x = area.Right - width - margin; // taskbar on the right
        Position = new PixelPoint(x, y);
        _positioned = true;
    }
}
