using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using Avalonia;
using Avalonia.Media.Imaging;
using TeleSave.Core;
using TeleSave.Shell;

namespace TeleSave.Linux;

/// <summary>Linux implementation, built on freedesktop standards and common CLI tools (optional ones degrade gracefully).</summary>
public sealed class LinuxPlatform : IShellPlatform
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string Xdg(string variable, string fallback) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value ? value : Path.Combine(Home, fallback);

    public string DataDirectory { get; } = Path.Combine(Xdg("XDG_DATA_HOME", ".local/share"), "TeleSave");
    private static string ConfigHome => Xdg("XDG_CONFIG_HOME", ".config");
    private static string DataHome => Xdg("XDG_DATA_HOME", ".local/share");
    public string ExecutablePath { get; } = Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } appImage
        ? appImage : Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "telesave");

    public ISecretStore Secrets { get; }
    public IPowerManager Power { get; } = new SystemdInhibitPower();

    public LinuxPlatform()
    {
        var fallback = new FileSecretStore(Path.Combine(DataDirectory, "secrets"));
        Secrets = Tool.Exists("secret-tool") ? new SecretToolStore(fallback) : fallback;
    }

    public string DownloadsDirectory =>
        Tool.Run("xdg-user-dir", "DOWNLOAD") is { Length: > 0 } dir && Directory.Exists(dir.Trim()) ? dir.Trim() : Path.Combine(Home, "Downloads");

    /// <summary>LANGUAGE (colon list) or LC_ALL / LC_MESSAGES / LANG, e.g. "az_AZ.UTF-8" → "az-AZ".</summary>
    public IReadOnlyList<string> PreferredLanguages
    {
        get
        {
            var raw = new[] { "LANGUAGE", "LC_ALL", "LC_MESSAGES", "LANG" }
                .Select(Environment.GetEnvironmentVariable).FirstOrDefault(v => !string.IsNullOrEmpty(v) && v != "C" && v != "POSIX");
            var list = (raw ?? CultureInfo.CurrentUICulture.Name).Split(':', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Split('.', '@')[0].Replace('_', '-')).ToList();
            return list.Count > 0 ? list : [CultureInfo.CurrentUICulture.Name];
        }
    }

    // On X11 the pointer position could be queried; Wayland does not expose it. The panel then opens by the
    // top or bottom panel's clock corner, which is where tray icons live on most desktops.
    public PixelPoint? PointerPosition => null;

    private X11Shortcuts? _shortcuts;
    public TeleSave.UI.IGlobalShortcuts Shortcuts => _shortcuts ??= new X11Shortcuts();

    // MARK: Build-time metadata (same as Windows)

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

    // MARK: File manager "Send to TeleSave"

    private string NautilusScript(string title) => Path.Combine(DataHome, "nautilus", "scripts", title);
    private string CajaScript(string title) => Path.Combine(ConfigHome, "caja", "scripts", title);
    private string DolphinMenu6 => Path.Combine(DataHome, "kio", "servicemenus", "telesave.desktop");
    private string DolphinMenu5 => Path.Combine(DataHome, "kservices5", "ServiceMenus", "telesave.desktop");
    private string NemoAction => Path.Combine(DataHome, "nemo", "actions", "telesave.nemo_action");
    private string TitleFile => Path.Combine(DataDirectory, "send-to-title");

    /// <summary>
    /// Nautilus (GNOME Files) and Caja scripts, Dolphin (KDE) service menu, Nemo (Cinnamon) action. Every file
    /// manager also gets "Open With › TeleSave" from the application entry, which is always installed.
    /// </summary>
    public void SetSendToMenu(bool enabled, string title)
    {
        InstallApplicationEntry();
        // Scripts are named after the (localized) title; remove the previous one when the title changes.
        if (File.Exists(TitleFile) && File.ReadAllText(TitleFile) is { Length: > 0 } previous)
        {
            TryDelete(NautilusScript(previous));
            TryDelete(CajaScript(previous));
        }
        foreach (var path in new[] { DolphinMenu6, DolphinMenu5, NemoAction }) TryDelete(path);
        if (!enabled) { TryDelete(TitleFile); return; }

        var safeTitle = title.Replace('/', '-');
        var script = $"#!/bin/sh\nexec \"{ExecutablePath}\" --send \"$@\"\n";
        WriteExecutable(NautilusScript(safeTitle), script);
        WriteExecutable(CajaScript(safeTitle), script);
        var service = $"""
            [Desktop Entry]
            Type=Service
            MimeType=all/all;
            Actions=sendToTeleSave
            X-KDE-ServiceTypes=KonqPopupMenu/Plugin
            X-KDE-Priority=TopLevel

            [Desktop Action sendToTeleSave]
            Name={title}
            Icon=telesave
            Exec="{ExecutablePath}" --send %F
            """;
        WriteExecutable(DolphinMenu6, service);
        WriteExecutable(DolphinMenu5, service);
        Write(NemoAction, $"""
            [Nemo Action]
            Name={title}
            Comment={title}
            Exec="{ExecutablePath}" --send %F
            Icon-Name=telesave
            Selection=notnone
            Extensions=any;
            """);
        Write(TitleFile, safeTitle);
    }

    private void InstallApplicationEntry()
    {
        // Installed from a .deb/.rpm: the package already ships the menu entry and icon.
        if (File.Exists("/usr/share/applications/telesave.desktop")) return;
        var icon = Path.Combine(DataHome, "icons", "hicolor", "256x256", "apps", "telesave.png");
        try
        {
            if (!File.Exists(icon))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(icon)!);
                using var source = Avalonia.Platform.AssetLoader.Open(new Uri("avares://TeleSave.Shell/icon.png"));
                using var target = File.Create(icon);
                source.CopyTo(target);
            }
        }
        catch (Exception) { }
        Write(Path.Combine(DataHome, "applications", "telesave.desktop"), $"""
            [Desktop Entry]
            Type=Application
            Name=TeleSave
            Comment=Upload files to Telegram
            Exec="{ExecutablePath}" --send %F
            Icon=telesave
            Terminal=false
            Categories=Network;FileTransfer;
            MimeType=application/octet-stream;inode/directory;image/png;image/jpeg;video/mp4;application/pdf;
            StartupWMClass=telesave
            """);
    }

    public void Cleanup()
    {
        SetSendToMenu(false, "");
        SetAutostart(false);
        TryDelete(Path.Combine(DataHome, "applications", "telesave.desktop"));
    }

    // MARK: Autostart

    private static string AutostartFile => Path.Combine(ConfigHome, "autostart", "telesave.desktop");

    public bool IsAutostartEnabled => File.Exists(AutostartFile);

    public void SetAutostart(bool enabled)
    {
        if (!enabled) { TryDelete(AutostartFile); return; }
        Write(AutostartFile, $"""
            [Desktop Entry]
            Type=Application
            Name=TeleSave
            Exec="{ExecutablePath}" --autostart
            Icon=telesave
            X-GNOME-Autostart-enabled=true
            """);
    }

    // MARK: Shell

    public void Notify(string title, string body) => Tool.Start("notify-send", "--app-name=TeleSave", "--icon=telesave", title, body);

    public void OpenUrl(Uri url) => Tool.Start("xdg-open", url.ToString());

    /// <summary>org.freedesktop.FileManager1.ShowItems selects the file; otherwise open its folder.</summary>
    public void RevealFile(string path)
    {
        var uri = new Uri(path).AbsoluteUri;
        if (!Tool.Start("dbus-send", "--session", "--dest=org.freedesktop.FileManager1", "--type=method_call",
                "/org/freedesktop/FileManager1", "org.freedesktop.FileManager1.ShowItems", $"array:string:{uri}", "string:"))
            Tool.Start("xdg-open", Path.GetDirectoryName(path)!);
    }

    public Bitmap? FileIcon(string path, int size)
    {
        try
        {
            if (!FileTypes.IsImage(path) || !File.Exists(path) || new FileInfo(path).Length > 40 * 1024 * 1024) return null;
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, size);
        }
        catch (Exception) { return null; }
    }

    /// <summary>Wayland (wl-paste) or X11 (xclip): copied files as text/uri-list, or a PNG image.</summary>
    public (IReadOnlyList<string> Files, byte[]? Png) ReadClipboard()
    {
        var wayland = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")) && Tool.Exists("wl-paste");
        string[] ListTypes() => (wayland ? Tool.Run("wl-paste", "--list-types") : Tool.Run("xclip", "-selection", "clipboard", "-t", "TARGETS", "-o"))
            ?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [];
        var types = ListTypes();
        if (types.Contains("text/uri-list"))
        {
            var text = wayland ? Tool.Run("wl-paste", "--type", "text/uri-list") : Tool.Run("xclip", "-selection", "clipboard", "-t", "text/uri-list", "-o");
            var files = (text ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(l => l.StartsWith("file://")).Select(l => Uri.UnescapeDataString(new Uri(l).LocalPath)).ToList();
            if (files.Count > 0) return (files, null);
        }
        if (types.Contains("image/png"))
        {
            var png = wayland ? Tool.RunBytes("wl-paste", "--type", "image/png") : Tool.RunBytes("xclip", "-selection", "clipboard", "-t", "image/png", "-o");
            if (png is { Length: > 0 }) return ([], png);
        }
        return ([], null);
    }

    // MARK: Helpers

    private static void Write(string path, string content)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content.Replace("\r\n", "\n"));
        }
        catch (Exception) { }
    }

    private static void WriteExecutable(string path, string content)
    {
        Write(path, content);
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.OtherRead); }
        catch (Exception) { }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (Exception) { }
    }
}

internal static class Tool
{
    public static bool Exists(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Any(dir => File.Exists(Path.Combine(dir, name)));

    public static bool Start(string name, params string[] args)
    {
        if (!Exists(name)) return false;
        try
        {
            var info = new ProcessStartInfo(name) { UseShellExecute = false };
            foreach (var arg in args) info.ArgumentList.Add(arg);
            using var process = Process.Start(info);
            return process is not null;
        }
        catch (Exception) { return false; }
    }

    public static string? Run(string name, params string[] args) => RunBytes(name, args) is { } bytes ? Encoding.UTF8.GetString(bytes) : null;

    public static byte[]? RunBytes(string name, params string[] args)
    {
        if (!Exists(name)) return null;
        try
        {
            var info = new ProcessStartInfo(name) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args) info.ArgumentList.Add(arg);
            using var process = Process.Start(info)!;
            using var buffer = new MemoryStream();
            process.StandardOutput.BaseStream.CopyTo(buffer);
            if (!process.WaitForExit(3000)) { process.Kill(); return null; }
            return process.ExitCode == 0 ? buffer.ToArray() : null;
        }
        catch (Exception) { return null; }
    }
}

/// <summary>GNOME Keyring / KWallet through libsecret's secret-tool; falls back to files if the keyring is unavailable.</summary>
internal sealed class SecretToolStore(ISecretStore fallback) : ISecretStore
{
    public byte[]? Read(string account)
    {
        var value = Tool.Run("secret-tool", "lookup", "application", "telesave", "account", account);
        if (value is { Length: > 0 }) { try { return Convert.FromBase64String(value.Trim()); } catch (FormatException) { } }
        return fallback.Read(account);
    }

    public bool Write(string account, byte[] data)
    {
        try
        {
            var info = new ProcessStartInfo("secret-tool") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "store", $"--label=TeleSave {account}", "application", "telesave", "account", account }) info.ArgumentList.Add(arg);
            using var process = Process.Start(info)!;
            process.StandardInput.Write(Convert.ToBase64String(data));
            process.StandardInput.Close();
            if (process.WaitForExit(5000) && process.ExitCode == 0) { fallback.Delete(account); return true; }
        }
        catch (Exception) { }
        return fallback.Write(account, data);
    }

    public void Delete(string account)
    {
        Tool.Run("secret-tool", "clear", "application", "telesave", "account", account);
        fallback.Delete(account);
    }
}

/// <summary>Files readable only by the user (mode 0600), as Telegram Desktop stores its own data.</summary>
internal sealed class FileSecretStore(string directory) : ISecretStore
{
    private string FileFor(string account) => Path.Combine(directory, account);

    public byte[]? Read(string account)
    {
        try { return File.Exists(FileFor(account)) ? File.ReadAllBytes(FileFor(account)) : null; } catch (Exception) { return null; }
    }

    public bool Write(string account, byte[] data)
    {
        try
        {
            Directory.CreateDirectory(directory);
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.WriteAllBytes(FileFor(account), data);
            File.SetUnixFileMode(FileFor(account), UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return true;
        }
        catch (Exception) { return false; }
    }

    public void Delete(string account)
    {
        try { File.Delete(FileFor(account)); } catch (Exception) { }
    }
}

/// <summary>Holds a systemd-logind sleep inhibitor while uploads run (no-op without systemd).</summary>
internal sealed class SystemdInhibitPower : IPowerManager
{
    private readonly object _gate = new();
    private int _holders;
    private Process? _inhibitor;

    public IDisposable KeepAwake(string reason)
    {
        lock (_gate)
        {
            if (_holders++ == 0 && Tool.Exists("systemd-inhibit"))
            {
                try
                {
                    var info = new ProcessStartInfo("systemd-inhibit") { UseShellExecute = false };
                    foreach (var arg in new[] { "--what=sleep:idle", "--who=TeleSave", $"--why={reason}", "--mode=block", "sleep", "infinity" }) info.ArgumentList.Add(arg);
                    _inhibitor = Process.Start(info);
                }
                catch (Exception) { _inhibitor = null; }
            }
        }
        return new Token(this);
    }

    private void Release()
    {
        lock (_gate)
        {
            if (--_holders > 0) return;
            try { _inhibitor?.Kill(entireProcessTree: true); } catch (Exception) { }
            _inhibitor?.Dispose();
            _inhibitor = null;
        }
    }

    private sealed class Token(SystemdInhibitPower owner) : IDisposable
    {
        private int _disposed;
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) owner.Release(); }
    }
}
