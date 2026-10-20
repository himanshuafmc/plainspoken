namespace Plainspoken.Core.Dictation;

public enum HoldAction
{
    None,

    /// <summary>The key has been held alone long enough: start recording.</summary>
    Start,

    /// <summary>The key was let go after recording started: stop and transcribe.</summary>
    Stop,
}

/// <summary>
/// Hold-to-talk on a modifier key such as Right Ctrl. Holding the key on its own for <see cref="Delay"/> starts
/// recording and letting go stops it. Pressing any other key first (Ctrl+C, Ctrl+V…) makes it an ordinary shortcut,
/// and a quick tap does nothing, so the key keeps working normally. Not thread-safe: call from one thread.
/// </summary>
public sealed class HoldToTalk
{
    public static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(300);

    private enum Phase
    {
        Up,
        Pressed,
        Holding,
        Shortcut,
    }

    private Phase _phase = Phase.Up;
    private DateTimeOffset _pressedAt;

    public bool IsHolding => _phase == Phase.Holding;

    /// <summary>The hold key went down. Key repeats while it stays down are ignored.</summary>
    public void KeyDown(DateTimeOffset now)
    {
        if (_phase == Phase.Up)
        {
            _phase = Phase.Pressed;
            _pressedAt = now;
        }
    }

    /// <summary>Another key went down while the hold key was down.</summary>
    public void OtherKeyDown()
    {
        if (_phase == Phase.Pressed)
        {
            _phase = Phase.Shortcut;
        }
    }

    /// <summary>Call a few times a second while the key is down.</summary>
    public HoldAction Poll(DateTimeOffset now)
    {
        if (_phase == Phase.Pressed && now - _pressedAt >= Delay)
        {
            _phase = Phase.Holding;
            return HoldAction.Start;
        }

        return HoldAction.None;
    }

    /// <summary>The hold key went up.</summary>
    public HoldAction KeyUp(DateTimeOffset now)
    {
        var action = Poll(now) == HoldAction.Start ? HoldAction.None : _phase == Phase.Holding ? HoldAction.Stop : HoldAction.None;
        _phase = Phase.Up;
        return action;
    }

    /// <summary>Forget the current press (for example when the setting changes).</summary>
    public void Reset() => _phase = Phase.Up;
}
