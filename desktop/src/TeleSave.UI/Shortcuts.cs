using Avalonia.Input;

namespace TeleSave.UI;

[Flags]
public enum ShortcutModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    /// <summary>The Windows key (Super on Linux).</summary>
    Super = 8,
}

public enum ShortcutAction
{
    UploadClipboard = 1,
    TogglePanel = 2,
}

/// <summary>A system-wide key combination. <see cref="Key"/> is "A"–"Z", "0"–"9", "F1"–"F24" or "Space".</summary>
public sealed record KeyShortcut(ShortcutModifiers Modifiers, string Key)
{
    private const ShortcutModifiers BottomLeftThree = ShortcutModifiers.Control | ShortcutModifiers.Super | ShortcutModifiers.Alt;
    /// <summary>Ctrl+Win+Alt+V: the three keys next to each other at the bottom left (⌃⌥⌘ on a Mac), plus V for paste.</summary>
    public static readonly KeyShortcut DefaultUpload = new(BottomLeftThree, "V");
    /// <summary>Ctrl+Win+Alt+T: T for TeleSave.</summary>
    public static readonly KeyShortcut DefaultPanel = new(BottomLeftThree, "T");

    /// <summary>In keyboard order: Ctrl+Win+Alt+Shift+T.</summary>
    public string Display(bool windows)
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ShortcutModifiers.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ShortcutModifiers.Super)) parts.Add(windows ? "Win" : "Super");
        if (Modifiers.HasFlag(ShortcutModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ShortcutModifiers.Shift)) parts.Add("Shift");
        parts.Add(Key);
        return string.Join("+", parts);
    }

    /// <summary>The shortcut for a key press, or null if the key is not supported or no Ctrl/Alt/Win is held.</summary>
    public static KeyShortcut? From(Key key, KeyModifiers modifiers)
    {
        var mods = ShortcutModifiers.None;
        if (modifiers.HasFlag(KeyModifiers.Control)) mods |= ShortcutModifiers.Control;
        if (modifiers.HasFlag(KeyModifiers.Alt)) mods |= ShortcutModifiers.Alt;
        if (modifiers.HasFlag(KeyModifiers.Shift)) mods |= ShortcutModifiers.Shift;
        if (modifiers.HasFlag(KeyModifiers.Meta)) mods |= ShortcutModifiers.Super;
        // Shift alone would swallow normal typing everywhere.
        if ((mods & (ShortcutModifiers.Control | ShortcutModifiers.Alt | ShortcutModifiers.Super)) == 0) return null;
        var name = key switch
        {
            >= Avalonia.Input.Key.A and <= Avalonia.Input.Key.Z => key.ToString(),
            >= Avalonia.Input.Key.D0 and <= Avalonia.Input.Key.D9 => ((int)(key - Avalonia.Input.Key.D0)).ToString(),
            >= Avalonia.Input.Key.F1 and <= Avalonia.Input.Key.F24 => key.ToString(),
            Avalonia.Input.Key.Space => "Space",
            _ => null,
        };
        return name is null ? null : new KeyShortcut(mods, name);
    }
}

/// <summary>System-wide shortcuts. Implemented per OS; <see cref="IsSupported"/> is false where apps cannot register them (Wayland).</summary>
public interface IGlobalShortcuts
{
    bool IsSupported { get; }
    /// <summary>Raised on any thread.</summary>
    event Action<ShortcutAction>? Pressed;
    /// <summary>Replaces the registered shortcuts. Returns the actions whose shortcut another app already holds.</summary>
    IReadOnlySet<ShortcutAction> Apply(IReadOnlyDictionary<ShortcutAction, KeyShortcut> shortcuts);
    /// <summary>While a new shortcut is being recorded, the current ones must not fire.</summary>
    void Suspend(bool suspended);
}

/// <summary>For platforms (and tests) without global shortcuts.</summary>
public sealed class NoGlobalShortcuts : IGlobalShortcuts
{
    public bool IsSupported => false;
    public event Action<ShortcutAction>? Pressed { add { } remove { } }
    public IReadOnlySet<ShortcutAction> Apply(IReadOnlyDictionary<ShortcutAction, KeyShortcut> shortcuts) => new HashSet<ShortcutAction>();
    public void Suspend(bool suspended) { }
}
