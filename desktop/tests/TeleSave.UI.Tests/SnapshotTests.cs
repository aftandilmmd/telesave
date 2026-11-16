using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Xunit;
using TeleSave.Core;
using TeleSave.UI;
using TeleSave.UI.Views;

[assembly: AvaloniaTestApplication(typeof(TeleSave.UI.Tests.TestApp))]

namespace TeleSave.UI.Tests;

public sealed class TestApp : Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApp>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>
/// Renders the panel in its main states with a fake Telegram client and saves PNGs to
/// tests/TeleSave.UI.Tests/snapshots (for visual review; also asserts that each state builds).
/// </summary>
public class SnapshotTests
{
    private static readonly string Output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "snapshots"));

    private static (AppModel Model, FakePlatform Platform) LoggedIn(AuthState? state = null, bool showChatFiles = true, string language = "tr")
    {
        var platform = new FakePlatform();
        CredentialsResolver.Save(new ApiCredentials(1234567, "0123456789abcdef0123456789abcdef"), platform.Secrets);
        new AppSettings { ShowChatFiles = showChatFiles, Language = language }.Save(Path.Combine(platform.DataDirectory, "settings.json"));
        var model = new AppModel(platform, a => Dispatcher.UIThread.Post(a), (_, onEvent) => new FakeClient(onEvent, state), start: true, backgroundChecks: false);
        Pump();
        return (model, platform);
    }

    private static void Pump()
    {
        for (var i = 0; i < 20; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
    }

    private static void Snapshot(Control view, string name)
    {
        var window = new Window { Content = view, Width = 360, Height = 480, SizeToContent = SizeToContent.Manual };
        window.Show();
        Pump();
        Directory.CreateDirectory(Output);
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(Output, name + ".png"));
        window.Close();
    }

    [AvaloniaFact]
    public void FirstRunApiKey()
    {
        var platform = new FakePlatform();
        new AppSettings { Language = "tr" }.Save(Path.Combine(platform.DataDirectory, "settings.json"));
        var model = new AppModel(platform, a => Dispatcher.UIThread.Post(a), start: true, backgroundChecks: false);
        Assert.Equal(AppModel.AppPhase.NeedsCredentials, model.Phase);
        Snapshot(new PanelView(model), "01-api-key");
    }

    [AvaloniaFact]
    public void LoginPhone()
    {
        var (model, _) = LoggedIn(new AuthState.WaitPhoneNumber());
        Snapshot(new PanelView(model), "02-login");
    }

    [AvaloniaFact]
    public void LoginQr()
    {
        var (model, _) = LoggedIn(new AuthState.WaitQrConfirmation("tg://login?token=AQIDBAUGBwgJCgsMDQ4PEA"));
        Snapshot(new PanelView(model), "03-login-qr");
    }

    [AvaloniaFact]
    public void MainWithChatFiles()
    {
        var (model, _) = LoggedIn();
        Assert.True(model.CanUpload);
        var panel = new PanelView(model);
        Snapshot(panel, "04-main-chat-files");
        Assert.Equal(5, model.ChatFiles.Count);
    }

    [AvaloniaFact]
    public void MainUploadsOnly()
    {
        var (model, _) = LoggedIn(showChatFiles: false);
        Snapshot(new PanelView(model), "05-main-uploads");
    }

    [AvaloniaFact]
    public void Settings()
    {
        var (model, _) = LoggedIn();
        var panel = new PanelView(model);
        panel.Navigate(PanelView.Page.Settings);
        Snapshot(panel, "06-settings");
    }

    [AvaloniaFact]
    public void DestinationPicker()
    {
        var (model, _) = LoggedIn();
        var panel = new PanelView(model);
        panel.Navigate(PanelView.Page.Destination);
        Snapshot(panel, "07-destination");
    }

    [AvaloniaFact]
    public void EnglishAndAzerbaijani()
    {
        var (en, _) = LoggedIn(language: "en");
        Snapshot(new PanelView(en), "08-main-en");
        var (az, _) = LoggedIn(language: "az");
        Snapshot(new PanelView(az), "09-main-az");
        L10n.SelectedCode = "tr";
    }

    [AvaloniaFact]
    public async Task ChoosingAChannelWithoutRightsIsRefused()
    {
        var (model, _) = LoggedIn();
        await model.RefreshChatsAsync();
        Assert.False(await model.ChooseAsync(model.Chats.First(c => c.Kind == ChatKind.Channel)));
        Assert.NotNull(model.DestinationError);
        Assert.True(model.Destination.IsSavedMessages);
        Assert.True(await model.ChooseAsync(model.Chats.First(c => c.Title == "Ailə")));
        Assert.Equal("Ailə", model.Destination.Title);
    }

    [AvaloniaFact]
    public void ContextMenuSettingReachesPlatform()
    {
        var (model, platform) = LoggedIn();
        model.ApplyContextMenu();
        Assert.Equal((true, "TeleSave'e gönder"), platform.SendToMenu);
        model.UpdateSettings(s => s with { ContextMenuEnabled = false });
        Assert.Equal(false, platform.SendToMenu?.Enabled);
    }
}
