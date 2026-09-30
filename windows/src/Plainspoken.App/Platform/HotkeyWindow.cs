using System.Runtime.InteropServices;
using Plainspoken.Core.Settings;

namespace Plainspoken.App.Platform;

/// <summary>
/// Hidden window that owns global hotkeys (RegisterHotKey) and raises <see cref="Pressed"/>
/// on the UI thread. Every registration is released in <see cref="Dispose"/>.
/// </summary>
internal sealed class HotkeyWindow : NativeWindow, IDisposable
{
    public const int ToggleId = 1;
    public const int CancelId = 2;

    private readonly HashSet<int> _registered = [];

    public HotkeyWindow() => CreateHandle(new CreateParams { Caption = "Plainspoken hotkeys" });

    public event EventHandler<int>? Pressed;

    /// <summary>Registers (or re-registers) <paramref name="id"/>. Returns false with a friendly reason on failure.</summary>
    public bool Register(int id, HotkeyModifiers modifiers, Keys key, out string? error)
    {
        Unregister(id);
        var mods = (uint)modifiers | NativeMethods.MOD_NOREPEAT;
        if (NativeMethods.RegisterHotKey(Handle, id, mods, (uint)(key & Keys.KeyCode)))
        {
            _registered.Add(id);
            error = null;
            return true;
        }

        var code = Marshal.GetLastWin32Error();
        error = code == NativeMethods.ERROR_HOTKEY_ALREADY_REGISTERED
            ? "is already used by another app"
            : $"could not be registered (error {code})";
        return false;
    }

    public void Unregister(int id)
    {
        if (_registered.Remove(id))
        {
            NativeMethods.UnregisterHotKey(Handle, id);
        }
    }

    public void Dispose()
    {
        foreach (var id in _registered.ToArray())
        {
            Unregister(id);
        }

        DestroyHandle();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == NativeMethods.WM_HOTKEY)
        {
            Pressed?.Invoke(this, (int)m.WParam);
            return;
        }

        base.WndProc(ref m);
    }
}

/// <summary>Maps between the Core hotkey string ("Ctrl+Alt+Space") and WinForms keys.</summary>
internal static class HotkeyKeys
{
    private static readonly HashSet<Keys> ModifierKeys =
    [
        Keys.ControlKey, Keys.LControlKey, Keys.RControlKey, Keys.ShiftKey, Keys.LShiftKey, Keys.RShiftKey,
        Keys.Menu, Keys.LMenu, Keys.RMenu, Keys.LWin, Keys.RWin, Keys.None, Keys.Apps,
    ];

    public static bool TryResolve(string text, out HotkeyModifiers modifiers, out Keys key, out string? error)
    {
        modifiers = HotkeyModifiers.None;
        key = Keys.None;
        if (!HotkeyGesture.TryParse(text, out var gesture, out error))
        {
            return false;
        }

        if (!Enum.TryParse(gesture.Key, ignoreCase: true, out key) || !Enum.IsDefined(key) || ModifierKeys.Contains(key) ||
            (key & ~Keys.KeyCode) != 0)
        {
            error = $"\"{gesture.Key}\" is not a key Plainspoken can use.";
            return false;
        }

        modifiers = gesture.Modifiers;
        return true;
    }

    /// <summary>Builds a gesture from a KeyDown in the hotkey picker; null while only modifiers are held.</summary>
    public static string? FromKeyEvent(KeyEventArgs e, bool winDown)
    {
        ArgumentNullException.ThrowIfNull(e);
        var key = e.KeyCode;
        if (ModifierKeys.Contains(key))
        {
            return null;
        }

        var mods = HotkeyModifiers.None;
        if (e.Control)
        {
            mods |= HotkeyModifiers.Ctrl;
        }

        if (e.Alt)
        {
            mods |= HotkeyModifiers.Alt;
        }

        if (e.Shift)
        {
            mods |= HotkeyModifiers.Shift;
        }

        if (winDown)
        {
            mods |= HotkeyModifiers.Win;
        }

        return new HotkeyGesture(mods, key.ToString()).ToString();
    }

    /// <summary>Human-friendly label, e.g. "Ctrl+Alt+Space", "Ctrl+Alt+1".</summary>
    public static string Display(string hotkey)
    {
        if (!HotkeyGesture.TryParse(hotkey, out var g, out _))
        {
            return hotkey;
        }

        var k = g.Key;
        if (k.Length == 2 && k[0] == 'D' && char.IsAsciiDigit(k[1]))
        {
            k = k[1..];
        }

        return new HotkeyGesture(g.Modifiers, k).ToString();
    }
}
