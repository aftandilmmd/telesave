using TeleSave.Shell;
using TeleSave.Windows.Platform;

namespace TeleSave.Windows;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args) => ShellHost.Run(args, () => new WindowsPlatform());
}
