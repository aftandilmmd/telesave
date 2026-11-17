using System.Runtime.InteropServices;
using TeleSave.UI;

namespace TeleSave.Windows.Platform;

/// <summary>
/// Global shortcuts through RegisterHotKey on a message-only window. Created on the UI thread, whose message
/// loop delivers WM_HOTKEY.
/// </summary>
public sealed class WindowsShortcuts : System.Windows.Forms.NativeWindow, IGlobalShortcuts
{
    private const int WmHotKey = 0x0312;
    private const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;
    private static readonly IntPtr HwndMessage = new(-3);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly HashSet<ShortcutAction> _registered = [];
    private IReadOnlyDictionary<ShortcutAction, KeyShortcut> _wanted = new Dictionary<ShortcutAction, KeyShortcut>();
    private HashSet<ShortcutAction> _failed = [];
    private bool _suspended;

    public bool IsSupported => true;
    public event Action<ShortcutAction>? Pressed;

    public IReadOnlySet<ShortcutAction> Apply(IReadOnlyDictionary<ShortcutAction, KeyShortcut> shortcuts)
    {
        _wanted = shortcuts;
        if (_suspended) return _failed;
        if (Handle == IntPtr.Zero) CreateHandle(new System.Windows.Forms.CreateParams { Parent = HwndMessage });
        UnregisterAll();
        _failed = [];
        foreach (var (action, shortcut) in shortcuts)
        {
            if (VirtualKey(shortcut.Key) is { } vk && RegisterHotKey(Handle, (int)action, Modifiers(shortcut.Modifiers) | ModNoRepeat, vk))
                _registered.Add(action);
            else
                _failed.Add(action);
        }
        return _failed;
    }

    public void Suspend(bool suspended)
    {
        _suspended = suspended;
        if (suspended) UnregisterAll();
        else Apply(_wanted);
    }

    private void UnregisterAll()
    {
        foreach (var action in _registered) UnregisterHotKey(Handle, (int)action);
        _registered.Clear();
    }

    protected override void WndProc(ref System.Windows.Forms.Message m)
    {
        if (m.Msg == WmHotKey && Enum.IsDefined(typeof(ShortcutAction), (int)m.WParam))
            Pressed?.Invoke((ShortcutAction)(int)m.WParam);
        base.WndProc(ref m);
    }

    private static uint Modifiers(ShortcutModifiers modifiers) =>
        (modifiers.HasFlag(ShortcutModifiers.Control) ? ModControl : 0) |
        (modifiers.HasFlag(ShortcutModifiers.Alt) ? ModAlt : 0) |
        (modifiers.HasFlag(ShortcutModifiers.Shift) ? ModShift : 0) |
        (modifiers.HasFlag(ShortcutModifiers.Super) ? ModWin : 0);

    /// <summary>"A"–"Z" and "0"–"9" are their ASCII codes; F1 = 0x70; Space = 0x20.</summary>
    private static uint? VirtualKey(string key) => key switch
    {
        [var c] when c is >= 'A' and <= 'Z' or >= '0' and <= '9' => c,
        "Space" => 0x20,
        ['F', ..] when int.TryParse(key[1..], out var n) && n is >= 1 and <= 24 => (uint)(0x70 + n - 1),
        _ => null,
    };
}
