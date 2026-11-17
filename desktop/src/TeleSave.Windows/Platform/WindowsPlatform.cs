using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Media.Imaging;
using Microsoft.Toolkit.Uwp.Notifications;
using Microsoft.Win32;
using TeleSave.Core;
using TeleSave.Shell;
using TeleSave.UI;

namespace TeleSave.Windows.Platform;

/// <summary>Windows implementation of everything the UI needs from the OS.</summary>
public sealed class WindowsPlatform : IShellPlatform
{
    public string DataDirectory { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TeleSave");
    public string DownloadsDirectory => KnownFolders.Downloads ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    public ISecretStore Secrets { get; }
    public IPowerManager Power { get; } = new WindowsPower();
    public IReadOnlyList<string> PreferredLanguages => UserLanguages.Get();
    public string ExecutablePath { get; } = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "TeleSave.exe");

    public WindowsPlatform() => Secrets = new DpapiSecretStore(Path.Combine(DataDirectory, "secrets"));

    private WindowsShortcuts? _shortcuts;
    /// <summary>Created on first use, which is on the UI thread (its message loop delivers the hotkeys).</summary>
    public IGlobalShortcuts Shortcuts => _shortcuts ??= new WindowsShortcuts();

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorPoint { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out CursorPoint point);

    public Avalonia.PixelPoint? PointerPosition => GetCursorPos(out var p) ? new Avalonia.PixelPoint(p.X, p.Y) : null;

    // MARK: Build-time metadata

    private static string? Metadata(string key)
    {
        var value = Assembly.GetEntryAssembly()?.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == key)?.Value;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public ApiCredentials? BundledCredentials =>
        int.TryParse(Metadata("TSApiID"), out var id) && Metadata("TSApiHash") is { } obf && Obfuscator.Decode(obf) is { } hash
            ? new ApiCredentials(id, hash) : null;

    public (string? Urls, string? PublicKey) RemoteConfig => (Metadata("TSConfigURLs"), Metadata("TSConfigPublicKey"));

    public string AppVersion
    {
        get
        {
            var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
            var plus = version.IndexOf('+');
            return plus > 0 ? version[..plus] : version;
        }
    }

    // MARK: Explorer "Send to TeleSave"

    private static readonly string[] VerbKeys = [@"Software\Classes\*\shell\TeleSave", @"Software\Classes\Directory\shell\TeleSave"];

    private static string SendToShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), "TeleSave.lnk");

    /// <summary>
    /// Classic right-click verb (Windows 10; "Show more options" on Windows 11) for files and folders, plus a
    /// Send-to shortcut that passes all selected files at once. Removed again when disabled.
    /// </summary>
    public void SetSendToMenu(bool enabled, string title)
    {
        foreach (var key in VerbKeys)
        {
            if (!enabled) { Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false); continue; }
            using var verb = Registry.CurrentUser.CreateSubKey(key);
            verb.SetValue("MUIVerb", title);
            verb.SetValue("Icon", $"\"{ExecutablePath}\",0");
            verb.SetValue("MultiSelectModel", "Player");
            using var command = verb.CreateSubKey("command");
            command.SetValue("", $"\"{ExecutablePath}\" --send \"%1\"");
        }
        try
        {
            if (!enabled) { File.Delete(SendToShortcut); return; }
            Shortcut.Create(SendToShortcut, ExecutablePath, "--send", title);
        }
        catch (Exception) { /* the verb alone still works */ }
    }

    /// <summary>Removes everything TeleSave registered (used by the uninstaller).</summary>
    public void Cleanup()
    {
        SetSendToMenu(false, "");
        SetAutostart(false);
        try { ToastNotificationManagerCompat.Uninstall(); } catch (Exception) { }
    }

    // MARK: Autostart

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public bool IsAutostartEnabled
    {
        get
        {
            using var run = Registry.CurrentUser.OpenSubKey(RunKey);
            return run?.GetValue("TeleSave") is string;
        }
    }

    public void SetAutostart(bool enabled)
    {
        using var run = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) run.SetValue("TeleSave", $"\"{ExecutablePath}\" --autostart");
        else run.DeleteValue("TeleSave", throwOnMissingValue: false);
    }

    // MARK: Shell

    public void Notify(string title, string body) =>
        new ToastContentBuilder().AddText(title).AddText(body).Show();

    public void OpenUrl(Uri url) => Process.Start(new ProcessStartInfo(url.ToString()) { UseShellExecute = true });

    public void RevealFile(string path) => Process.Start("explorer.exe", $"/select,\"{path}\"");

    private readonly Dictionary<string, Bitmap?> _iconCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Images get a real preview; other files the icon Windows associates with their type.</summary>
    public Bitmap? FileIcon(string path, int size)
    {
        try
        {
            if (FileTypes.IsImage(path) && File.Exists(path) && new FileInfo(path).Length < 40 * 1024 * 1024)
            {
                using var stream = File.OpenRead(path);
                return Bitmap.DecodeToWidth(stream, size);
            }
            var ext = Path.GetExtension(path);
            if (_iconCache.TryGetValue(ext, out var cached)) return cached;
            Bitmap? result = null;
            if (File.Exists(path) && System.Drawing.Icon.ExtractAssociatedIcon(path) is { } icon)
            {
                using (icon)
                using (var bmp = icon.ToBitmap())
                using (var ms = new MemoryStream())
                {
                    bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    ms.Position = 0;
                    result = new Bitmap(ms);
                }
            }
            _iconCache[ext] = result;
            return result;
        }
        catch (Exception) { return null; }
    }

    // MARK: Clipboard

    public (IReadOnlyList<string> Files, byte[]? Png) ReadClipboard()
    {
        try
        {
            if (System.Windows.Forms.Clipboard.ContainsFileDropList())
                return (System.Windows.Forms.Clipboard.GetFileDropList().Cast<string>().ToList(), null);
            if (System.Windows.Forms.Clipboard.ContainsImage() && System.Windows.Forms.Clipboard.GetImage() is { } image)
            {
                using (image)
                using (var ms = new MemoryStream())
                {
                    image.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    return ([], ms.ToArray());
                }
            }
        }
        catch (Exception) { }
        return ([], null);
    }
}

/// <summary>Secrets encrypted with DPAPI for the current Windows user (one file per account).</summary>
internal sealed class DpapiSecretStore(string directory) : ISecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("TeleSave.secrets.v1");

    private string FileFor(string account) => Path.Combine(directory, account + ".bin");

    public byte[]? Read(string account)
    {
        try { return File.Exists(FileFor(account)) ? ProtectedData.Unprotect(File.ReadAllBytes(FileFor(account)), Entropy, DataProtectionScope.CurrentUser) : null; }
        catch (Exception) { return null; }
    }

    public bool Write(string account, byte[] data)
    {
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(FileFor(account), ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser));
            return true;
        }
        catch (Exception) { return false; }
    }

    public void Delete(string account)
    {
        try { File.Delete(FileFor(account)); } catch (Exception) { }
    }
}

/// <summary>Keeps the PC awake (not the display) while uploads run.</summary>
internal sealed class WindowsPower : IPowerManager
{
    [Flags]
    private enum ExecutionState : uint { SystemRequired = 0x1, Continuous = 0x80000000 }

    [DllImport("kernel32.dll")]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState flags);

    private readonly object _gate = new();
    private int _holders;
    private Thread? _thread;
    private ManualResetEventSlim? _release;

    /// <summary>SetThreadExecutionState is per thread, so a dedicated thread holds the request.</summary>
    public IDisposable KeepAwake(string reason)
    {
        lock (_gate)
        {
            if (_holders++ == 0)
            {
                var release = _release = new ManualResetEventSlim();
                _thread = new Thread(() =>
                {
                    SetThreadExecutionState(ExecutionState.Continuous | ExecutionState.SystemRequired);
                    release.Wait();
                    SetThreadExecutionState(ExecutionState.Continuous);
                }) { IsBackground = true, Name = "TeleSave.KeepAwake" };
                _thread.Start();
            }
        }
        return new Token(this);
    }

    private void Release()
    {
        lock (_gate)
        {
            if (--_holders == 0) _release?.Set();
        }
    }

    private sealed class Token(WindowsPower owner) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Release(); }
    }
}

internal static class KnownFolders
{
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    public static string? Downloads
    {
        get
        {
            if (SHGetKnownFolderPath(DownloadsId, 0, IntPtr.Zero, out var ptr) != 0) return null;
            try { return Marshal.PtrToStringUni(ptr); } finally { Marshal.FreeCoTaskMem(ptr); }
        }
    }
}

internal static class UserLanguages
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetUserPreferredUILanguages(uint flags, out uint count, char[]? buffer, ref uint size);

    private const uint MuiLanguageName = 0x8;

    /// <summary>The user's Windows display languages in order, e.g. ["az-Latn-AZ", "en-US"].</summary>
    public static IReadOnlyList<string> Get()
    {
        try
        {
            uint size = 0;
            if (GetUserPreferredUILanguages(MuiLanguageName, out _, null, ref size) && size > 0)
            {
                var buffer = new char[size];
                if (GetUserPreferredUILanguages(MuiLanguageName, out _, buffer, ref size))
                    return new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);
            }
        }
        catch (Exception) { }
        return [CultureInfo.CurrentUICulture.Name];
    }
}

/// <summary>.lnk creation through the WScript.Shell COM object.</summary>
internal static class Shortcut
{
    public static void Create(string path, string target, string arguments, string description)
    {
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell unavailable");
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            dynamic link = shell.CreateShortcut(path);
            link.TargetPath = target;
            link.Arguments = arguments;
            link.Description = description;
            link.IconLocation = target + ",0";
            link.Save();
            Marshal.FinalReleaseComObject(link);
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }
}
