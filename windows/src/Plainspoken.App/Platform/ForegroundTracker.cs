namespace Plainspoken.App.Platform;

/// <summary>
/// Remembers the last "real" app window that had focus. Clicking the tray icon makes the taskbar
/// the foreground window, so when dictation was toggled from the tray we put focus back here
/// before pasting. Uses a WinEvent hook; callbacks arrive on the UI thread's message loop.
/// </summary>
internal sealed class ForegroundTracker : IDisposable
{
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland",
    };

    private readonly NativeMethods.WinEventDelegate _callback; // keep alive for the hook's lifetime
    private readonly IntPtr _hook;
    private readonly uint _ownPid = (uint)Environment.ProcessId;

    public ForegroundTracker()
    {
        _callback = OnForegroundChanged;
        _hook = NativeMethods.SetWinEventHook(NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _callback, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT | NativeMethods.WINEVENT_SKIPOWNPROCESS);
        Remember(NativeMethods.GetForegroundWindow());
    }

    public IntPtr LastExternalWindow { get; private set; }

    /// <summary>The taskbar/tray, or one of our own invisible windows (e.g. the tray menu owner).</summary>
    public bool IsTrayOrHidden(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return true;
        }

        if (ShellClasses.Contains(NativeMethods.ClassNameOf(hwnd)))
        {
            return true;
        }

        return NativeMethods.ProcessIdOf(hwnd) == _ownPid && !NativeMethods.IsWindowVisible(hwnd);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.UnhookWinEvent(_hook);
        }
    }

    private void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            Remember(hwnd);
        }
        catch (Exception)
        {
            // Called from a native callback: an escaping exception would crash the process.
        }
    }

    private void Remember(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero && !IsTrayOrHidden(hwnd) && NativeMethods.ProcessIdOf(hwnd) != _ownPid)
        {
            LastExternalWindow = hwnd;
        }
    }
}
