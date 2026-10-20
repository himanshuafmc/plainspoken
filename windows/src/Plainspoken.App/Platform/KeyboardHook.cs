using System.Runtime.InteropServices;

namespace Plainspoken.App.Platform;

/// <param name="VirtualKey">The Windows virtual-key code (left and right modifier keys are told apart).</param>
/// <param name="Down">True for a key press (or repeat), false for a release.</param>
/// <param name="Injected">Sent by a program (for example our own Ctrl+V), not typed.</param>
internal readonly record struct KeyChange(int VirtualKey, bool Down, bool Injected);

/// <summary>
/// A low-level keyboard hook (WH_KEYBOARD_LL) that only watches: every key still reaches the app it was meant for.
/// Used for hold-to-talk. Events arrive on the thread that created the hook (the UI thread) and must return quickly,
/// or Windows removes the hook.
/// </summary>
internal sealed class KeyboardHook : IDisposable
{
    private readonly NativeMethods.LowLevelKeyboardProc _proc; // keep alive for the hook's lifetime
    private IntPtr _hook;

    public KeyboardHook()
    {
        _proc = HookProc;
        _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _proc, NativeMethods.GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            Error = Marshal.GetLastWin32Error();
        }
    }

    public event EventHandler<KeyChange>? KeyChanged;

    /// <summary>The Windows error code if the hook could not be installed (0 = installed).</summary>
    public int Error { get; }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var msg = unchecked((int)wParam.ToInt64());
                var down = msg is NativeMethods.WM_KEYDOWN or NativeMethods.WM_SYSKEYDOWN;
                if (down || msg is NativeMethods.WM_KEYUP or NativeMethods.WM_SYSKEYUP)
                {
                    var data = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
                    KeyChanged?.Invoke(this, new KeyChange((int)data.vkCode, down, (data.flags & NativeMethods.LLKHF_INJECTED) != 0));
                }
            }
            catch (Exception)
            {
                // Called from a native callback: an escaping exception would crash the process.
            }
        }

        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}
