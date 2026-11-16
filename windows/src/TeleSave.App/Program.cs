using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Avalonia;
using TeleSave.App.Platform;

namespace TeleSave.App;

internal static class Program
{
    private static readonly string PipeName = $"TeleSave-{Environment.UserName}";
    private static Mutex? _instance;

    /// <summary>
    /// Arguments: <c>--send &lt;paths…&gt;</c> (Explorer menu / Send to), <c>--autostart</c> (launched at login),
    /// <c>--cleanup</c> (uninstaller: remove menu entries and autostart).
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Contains("--cleanup"))
        {
            new WindowsPlatform().Cleanup();
            return 0;
        }
        var paths = args.SkipWhile(a => a != "--send").Skip(1).Where(a => !a.StartsWith("--")).ToList();

        // One TeleSave per user: a second launch hands its files to the running one and exits.
        _instance = new Mutex(true, $"Local\\{PipeName}", out var first);
        if (!first)
        {
            Forward(paths);
            return 0;
        }

        App.InitialPaths = paths;
        App.LaunchedAtLogin = args.Contains("--autostart");
        App.PipeName = PipeName;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();

    private static void Forward(List<string> paths)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(3000);
            var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(paths));
            client.Write(payload);
        }
        catch (Exception) { /* the running instance is not listening; nothing else to do */ }
    }
}
