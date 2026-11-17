using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using TeleSave.UI;

namespace TeleSave.Linux;

/// <summary>
/// Global shortcuts on X11 with XGrabKey on the root window, on an own display connection and thread.
/// Wayland does not let apps grab keys: there the user binds <c>telesave --send-clipboard</c> in the desktop's
/// keyboard settings instead (<see cref="IsSupported"/> is false).
/// </summary>
public sealed class X11Shortcuts : IGlobalShortcuts
{
    private const string Lib = "libX11.so.6";
    [DllImport(Lib)] private static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport(Lib)] private static extern IntPtr XDefaultRootWindow(IntPtr display);
    [DllImport(Lib)] private static extern IntPtr XStringToKeysym(string name);
    [DllImport(Lib)] private static extern byte XKeysymToKeycode(IntPtr display, IntPtr keysym);
    [DllImport(Lib)] private static extern int XGrabKey(IntPtr display, int keycode, uint modifiers, IntPtr window, bool ownerEvents, int pointerMode, int keyboardMode);
    [DllImport(Lib)] private static extern int XUngrabKey(IntPtr display, int keycode, uint modifiers, IntPtr window);
    [DllImport(Lib)] private static extern int XSync(IntPtr display, bool discard);
    [DllImport(Lib)] private static extern int XPending(IntPtr display);
    [DllImport(Lib)] private static extern int XNextEvent(IntPtr display, IntPtr eventReturn);

    private delegate int ErrorHandler(IntPtr display, IntPtr errorEvent);
    [DllImport(Lib)] private static extern IntPtr XSetErrorHandler(IntPtr handler);

    private const uint ShiftMask = 1, LockMask = 2, ControlMask = 4, Mod1Mask = 8 /* Alt */, Mod2Mask = 16 /* NumLock */, Mod4Mask = 64 /* Super */;
    private const int KeyPress = 2, GrabModeAsync = 1;
    // Grabs are exact: also grab with Caps Lock / Num Lock on.
    private static readonly uint[] LockVariants = [0, LockMask, Mod2Mask, LockMask | Mod2Mask];

    private static volatile bool _grabFailed;
    private static readonly ErrorHandler OnError = (_, _) => { _grabFailed = true; return 0; };

    private readonly BlockingCollection<Action> _work = [];
    private readonly Dictionary<ShortcutAction, (int Keycode, uint Modifiers)> _grabbed = [];
    private IReadOnlyDictionary<ShortcutAction, KeyShortcut> _wanted = new Dictionary<ShortcutAction, KeyShortcut>();
    private HashSet<ShortcutAction> _failed = [];
    private bool _suspended;
    private IntPtr _display;
    private IntPtr _root;

    public event Action<ShortcutAction>? Pressed;

    public bool IsSupported { get; }

    public X11Shortcuts()
    {
        var wayland = string.Equals(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), "wayland", StringComparison.OrdinalIgnoreCase)
                      || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
        if (wayland || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))) return;
        try
        {
            _display = XOpenDisplay(IntPtr.Zero);
            if (_display == IntPtr.Zero) return;
            _root = XDefaultRootWindow(_display);
            IsSupported = true;
            new Thread(Loop) { IsBackground = true, Name = "TeleSave.X11Shortcuts" }.Start();
        }
        catch (Exception) { IsSupported = false; }
    }

    public IReadOnlySet<ShortcutAction> Apply(IReadOnlyDictionary<ShortcutAction, KeyShortcut> shortcuts)
    {
        _wanted = shortcuts;
        if (!IsSupported) return shortcuts.Keys.ToHashSet();
        if (_suspended) return _failed;
        return Run(() =>
        {
            UngrabAll();
            _failed = [];
            foreach (var (action, shortcut) in shortcuts)
            {
                if (Grab(shortcut) is { } grab) _grabbed[action] = grab;
                else _failed.Add(action);
            }
            return _failed;
        });
    }

    public void Suspend(bool suspended)
    {
        if (!IsSupported) return;
        _suspended = suspended;
        if (suspended) Run(() => { UngrabAll(); return _failed; });
        else Apply(_wanted);
    }

    /// <summary>Runs on the X thread (one display connection must not be shared across threads) and waits.</summary>
    private HashSet<ShortcutAction> Run(Func<HashSet<ShortcutAction>> work)
    {
        var done = new TaskCompletionSource<HashSet<ShortcutAction>>();
        _work.Add(() => { try { done.SetResult(work()); } catch (Exception e) { done.SetException(e); } });
        return done.Task.Wait(TimeSpan.FromSeconds(3)) ? done.Task.Result : [.. _wanted.Keys];
    }

    private (int, uint)? Grab(KeyShortcut shortcut)
    {
        var keysym = XStringToKeysym(shortcut.Key switch
        {
            "Space" => "space",
            [var c] when char.IsLetter(c) => char.ToLowerInvariant(c).ToString(),
            _ => shortcut.Key,
        });
        if (keysym == IntPtr.Zero) return null;
        int keycode = XKeysymToKeycode(_display, keysym);
        if (keycode == 0) return null;
        var mods = Mask(shortcut.Modifiers);
        // Another client holding the key reports BadAccess asynchronously: catch it with a temporary error handler.
        _grabFailed = false;
        var previous = XSetErrorHandler(Marshal.GetFunctionPointerForDelegate(OnError));
        foreach (var variant in LockVariants) XGrabKey(_display, keycode, mods | variant, _root, false, GrabModeAsync, GrabModeAsync);
        XSync(_display, false);
        XSetErrorHandler(previous);
        if (!_grabFailed) return (keycode, mods);
        foreach (var variant in LockVariants) XUngrabKey(_display, keycode, mods | variant, _root);
        XSync(_display, false);
        return null;
    }

    private void UngrabAll()
    {
        foreach (var (keycode, mods) in _grabbed.Values)
            foreach (var variant in LockVariants) XUngrabKey(_display, keycode, mods | variant, _root);
        _grabbed.Clear();
        XSync(_display, false);
    }

    private static uint Mask(ShortcutModifiers modifiers) =>
        (modifiers.HasFlag(ShortcutModifiers.Control) ? ControlMask : 0) |
        (modifiers.HasFlag(ShortcutModifiers.Alt) ? Mod1Mask : 0) |
        (modifiers.HasFlag(ShortcutModifiers.Shift) ? ShiftMask : 0) |
        (modifiers.HasFlag(ShortcutModifiers.Super) ? Mod4Mask : 0);

    private void Loop()
    {
        // XEvent is a 192-byte union on 64-bit; for XKeyEvent, state is at offset 80 and keycode at 84.
        var buffer = Marshal.AllocHGlobal(192);
        while (true)
        {
            try
            {
                while (_work.TryTake(out var job, 30)) job();
                while (XPending(_display) > 0)
                {
                    XNextEvent(_display, buffer);
                    if (Marshal.ReadInt32(buffer) != KeyPress) continue;
                    var state = (uint)Marshal.ReadInt32(buffer, 80) & ~(LockMask | Mod2Mask);
                    var keycode = Marshal.ReadInt32(buffer, 84);
                    foreach (var (action, grab) in _grabbed)
                        if (grab.Keycode == keycode && grab.Modifiers == state) Pressed?.Invoke(action);
                }
            }
            catch (Exception) { Thread.Sleep(200); }
        }
    }
}
