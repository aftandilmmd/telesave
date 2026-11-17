using System.Text.Json;
using System.Text.Json.Serialization;
using TeleSave.Core;

namespace TeleSave.UI;

public sealed record AppSettings
{
    public bool SendMediaCompressed { get; init; }
    public bool GroupAsAlbum { get; init; }
    public string CaptionTemplate { get; init; } = "";
    public int MaxConcurrent { get; init; } = 2;
    public bool NotificationsEnabled { get; init; } = true;
    public bool OpenPanelOnDrop { get; init; } = true;
    public FolderMode FolderMode { get; init; } = FolderMode.Zip;
    public bool ShowChatFiles { get; init; } = true;
    public string Language { get; init; } = L10n.SystemCode;
    /// <summary>"Send to TeleSave" in Explorer's right-click menu.</summary>
    public bool ContextMenuEnabled { get; init; } = true;
    /// <summary>Keep a taskbar button: dragging a file onto it opens the drop panel.</summary>
    public bool ShowInTaskbar { get; init; } = true;
    /// <summary>Thin drop strip at the screen edge.</summary>
    public bool EdgeShelf { get; init; }
    /// <summary>System-wide shortcut that uploads what is on the clipboard (null = off).</summary>
    public KeyShortcut? UploadShortcut { get; init; } = KeyShortcut.DefaultUpload;
    /// <summary>System-wide shortcut that shows/hides the panel (null = off).</summary>
    public KeyShortcut? PanelShortcut { get; init; } = KeyShortcut.DefaultPanel;
    /// <summary>Set once launch-at-login has been turned on by default on first start.</summary>
    public bool AutostartConfigured { get; init; }

    [JsonIgnore]
    public IReadOnlyDictionary<ShortcutAction, KeyShortcut> Shortcuts
    {
        get
        {
            var map = new Dictionary<ShortcutAction, KeyShortcut>();
            if (UploadShortcut is { } upload) map[ShortcutAction.UploadClipboard] = upload;
            if (PanelShortcut is { } panel) map[ShortcutAction.TogglePanel] = panel;
            return map;
        }
    }

    [JsonIgnore]
    public UploadQueue.Settings QueueSettings => new(Math.Clamp(MaxConcurrent, 1, 4), SendMediaCompressed, GroupAsAlbum, CaptionTemplate);

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static AppSettings Load(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new() : new(); }
        catch (Exception) { return new(); }
    }

    public void Save(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception) { }
    }
}
