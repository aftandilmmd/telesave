using TeleSave.Shell;
using TeleSave.Windows.Platform;

namespace TeleSave.Windows;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // The Store package starts at login through its startup task, which passes no arguments.
        if (WindowsPlatform.IsPackaged && StartedByStartupTask()) args = [.. args, "--autostart"];
        return ShellHost.Run(args, () => new WindowsPlatform());
    }

    private static bool StartedByStartupTask()
    {
        try { return global::Windows.ApplicationModel.AppInstance.GetActivatedEventArgs()?.Kind == global::Windows.ApplicationModel.Activation.ActivationKind.StartupTask; }
        catch (Exception) { return false; }
    }
}
