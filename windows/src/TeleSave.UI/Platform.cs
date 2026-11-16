using Avalonia.Media.Imaging;
using TeleSave.Core;

namespace TeleSave.UI;

/// <summary>Everything the UI needs from the operating system. Windows implements it in TeleSave.App.</summary>
public interface IPlatform
{
    /// <summary>Per-user data folder (TDLib database, queue, settings).</summary>
    string DataDirectory { get; }
    string DownloadsDirectory { get; }
    ISecretStore Secrets { get; }
    IPowerManager Power { get; }
    IReadOnlyList<string> PreferredLanguages { get; }

    /// <summary>Adds or removes "Send to TeleSave" in the file manager's right-click menu.</summary>
    void SetSendToMenu(bool enabled, string title);
    bool IsAutostartEnabled { get; }
    void SetAutostart(bool enabled);
    void Notify(string title, string body);
    void OpenUrl(Uri url);
    /// <summary>Opens the folder with the file selected.</summary>
    void RevealFile(string path);
    /// <summary>Preview or file-type icon for a local file; null if none.</summary>
    Bitmap? FileIcon(string path, int size);
    /// <summary>API key built into this build (from Secrets.env at build time), if any.</summary>
    ApiCredentials? BundledCredentials { get; }
    /// <summary>Remote config URLs and public key built into this build, if any.</summary>
    (string? Urls, string? PublicKey) RemoteConfig { get; }
    string AppVersion { get; }
    /// <summary>Files copied in the file manager, or an image (PNG bytes) on the clipboard.</summary>
    (IReadOnlyList<string> Files, byte[]? Png) ReadClipboard();
}
