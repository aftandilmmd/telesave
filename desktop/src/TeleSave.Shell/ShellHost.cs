using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Avalonia;
using TeleSave.UI;

namespace TeleSave.Shell;

/// <summary>What the shell needs beyond <see cref="IPlatform"/>.</summary>
public interface IShellPlatform : IPlatform
{
    /// <summary>Global pointer position in screen pixels, if the OS exposes it (not on Wayland).</summary>
    PixelPoint? PointerPosition { get; }
    /// <summary>Removes menu entries and autostart (uninstall).</summary>
    void Cleanup();
}

/// <summary>
/// Entry point shared by the Windows and Linux executables. Arguments: <c>--send &lt;paths…&gt;</c>
/// (file manager menu), <c>--autostart</c> (launched at login), <c>--cleanup</c> (uninstall).
/// </summary>
public static class ShellHost
{
    private static Mutex? _instance;

    public static int Run(string[] args, Func<IShellPlatform> createPlatform)
    {
        if (args.Contains("--cleanup"))
        {
            createPlatform().Cleanup();
            return 0;
        }
        var pipeName = $"TeleSave-{Environment.UserName}";
        var paths = args.SkipWhile(a => a != "--send").Skip(1).Where(a => !a.StartsWith("--")).ToList();

        // One TeleSave per user: a second launch hands its files to the running one and exits.
        _instance = new Mutex(true, pipeName, out var first);
        if (!first)
        {
            Forward(pipeName, paths);
            return 0;
        }

        ShellApp.CreatePlatform = createPlatform;
        ShellApp.InitialPaths = paths;
        ShellApp.LaunchedAtLogin = args.Contains("--autostart");
        ShellApp.PipeName = pipeName;
        return AppBuilder.Configure<ShellApp>().UsePlatformDetect().LogToTrace()
            .StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    private static void Forward(string pipeName, List<string> paths)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            client.Connect(3000);
            client.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(paths)));
        }
        catch (Exception) { /* the running instance is not listening */ }
    }
}
