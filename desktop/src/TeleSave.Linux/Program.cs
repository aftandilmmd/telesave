using TeleSave.Shell;

namespace TeleSave.Linux;

internal static class Program
{
    public static int Main(string[] args) => ShellHost.Run(args, () => new LinuxPlatform());
}
