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
        if (args.Contains("--self-test")) return SelfTest(createPlatform());
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

    /// <summary>
    /// Checks a published package without a display: TDLib loads, translations are found, and dates format in
    /// Turkish (needs ICU). Used by CI on the real Windows and Linux builds.
    /// </summary>
    private static int SelfTest(IShellPlatform platform)
    {
        try
        {
            Console.WriteLine(TeleSave.TdLib.TdLibInfo.SelfCheck());
            // Whether this build carries an API key (from the TG_API_ID / TG_API_HASH secrets); never prints the key.
            Console.WriteLine("bundled api key: " + (platform.BundledCredentials?.IsValid == true ? "yes" : "no"));
            var languages = TeleSave.Core.L10n.Languages.Select(l => l.Code).ToList();
            Console.WriteLine("languages: " + string.Join(",", languages));
            if (languages.Count < 4) throw new InvalidOperationException("translations missing");
            TeleSave.Core.L10n.SelectedCode = "tr";
            // Mid-month, so the time zone cannot move it into another month.
            var date = TeleSave.Core.Format.UploadDate(new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero));
            Console.WriteLine("tr date: " + date);
            if (!date.Contains("Eyl")) throw new InvalidOperationException("culture data missing (ICU)");
            Console.WriteLine("SELF-TEST PASSED");
            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine("SELF-TEST FAILED: " + e.Message);
            return 1;
        }
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
