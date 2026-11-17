using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using TeleSave.Core;
using TeleSave.TdLib;
using TeleSave.UI.Views;
using Xunit;

namespace TeleSave.UI.Tests;

public class ShortcutTests
{
    [Fact]
    public void KeyPressBecomesShortcut()
    {
        var s = KeyShortcut.From(Key.V, KeyModifiers.Control | KeyModifiers.Meta | KeyModifiers.Alt);
        Assert.Equal(KeyShortcut.DefaultUpload, s);
        Assert.Equal("Ctrl+Win+Alt+V", s!.Display(windows: true));
        Assert.Equal("Ctrl+Super+Alt+V", s.Display(windows: false));
        Assert.Equal(KeyShortcut.DefaultPanel, KeyShortcut.From(Key.T, KeyModifiers.Control | KeyModifiers.Meta | KeyModifiers.Alt));
        Assert.Equal("Ctrl+Shift+5", KeyShortcut.From(Key.D5, KeyModifiers.Control | KeyModifiers.Shift)!.Display(true));
        Assert.Equal("Alt+F9", KeyShortcut.From(Key.F9, KeyModifiers.Alt)!.Display(true));
        Assert.Null(KeyShortcut.From(Key.T, KeyModifiers.None));
        Assert.Null(KeyShortcut.From(Key.T, KeyModifiers.Shift));
        Assert.Null(KeyShortcut.From(Key.OemComma, KeyModifiers.Control));
    }

    [Fact]
    public void SettingsKeepTurnedOffShortcut()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        Assert.Equal(KeyShortcut.DefaultUpload, AppSettings.Load(path).UploadShortcut);
        Assert.Equal(KeyShortcut.DefaultPanel, AppSettings.Load(path).PanelShortcut);
        new AppSettings { UploadShortcut = null, PanelShortcut = new(ShortcutModifiers.Control | ShortcutModifiers.Alt, "P") }.Save(path);
        var loaded = AppSettings.Load(path);
        Assert.Null(loaded.UploadShortcut);
        Assert.Equal(new KeyShortcut(ShortcutModifiers.Control | ShortcutModifiers.Alt, "P"), loaded.PanelShortcut);
        File.Delete(path);
    }

    [AvaloniaFact]
    public void ShortcutUploadsClipboardAndReportsConflicts()
    {
        var platform = new FakePlatform();
        CredentialsResolver.Save(new ApiCredentials(1234567, "0123456789abcdef0123456789abcdef"), platform.Secrets);
        var file = Path.Combine(platform.DataDirectory, "note.txt");
        Directory.CreateDirectory(platform.DataDirectory);
        File.WriteAllText(file, "hello");
        platform.Clipboard = ([file], null);
        platform.FakeShortcuts.Taken.Add(ShortcutAction.TogglePanel);
        var model = new AppModel(platform, a => Dispatcher.UIThread.Post(a), (_, onEvent) => new FakeClient(onEvent, null), start: true, backgroundChecks: false);
        Pump();
        model.StartShortcuts();
        Assert.Equal(KeyShortcut.DefaultUpload, platform.FakeShortcuts.Registered[ShortcutAction.UploadClipboard]);

        var panelRequests = 0;
        model.PanelRequested += () => panelRequests++;
        platform.FakeShortcuts.Press(ShortcutAction.UploadClipboard);
        for (var i = 0; i < 100 && model.Items.Count == 0; i++) Pump();
        Assert.Contains(model.Items, item => item.DisplayName == "note.txt");
        Assert.Equal(0, panelRequests);

        model.UpdateSettings(s => s with { PanelShortcut = new(ShortcutModifiers.Control | ShortcutModifiers.Alt, "P") });
        Assert.Contains(ShortcutAction.TogglePanel, model.FailedShortcuts);
        Assert.NotNull(model.ShortcutConflict(ShortcutAction.TogglePanel));
        Assert.Null(model.ShortcutConflict(ShortcutAction.UploadClipboard));
        new PanelView(model).Navigate(PanelView.Page.Settings);
    }

    private static void Pump()
    {
        for (var i = 0; i < 20; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
    }
}
