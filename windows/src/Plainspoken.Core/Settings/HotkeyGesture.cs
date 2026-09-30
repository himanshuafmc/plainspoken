namespace Plainspoken.Core.Settings;

/// <summary>Modifier flags. Values match the Win32 MOD_* constants.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Ctrl = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>
/// A hotkey such as "Ctrl+Alt+Space". The key part is a platform key name
/// (on Windows, a <c>System.Windows.Forms.Keys</c> name); the app validates it.
/// </summary>
public readonly record struct HotkeyGesture(HotkeyModifiers Modifiers, string Key)
{
    public static bool TryParse(string? text, out HotkeyGesture gesture, out string? error)
    {
        gesture = default;
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Hotkey is empty.";
            return false;
        }

        var mods = HotkeyModifiers.None;
        string? key = null;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var mod = ParseModifier(part);
            if (mod != HotkeyModifiers.None)
            {
                mods |= mod;
            }
            else if (key is null)
            {
                key = part;
            }
            else
            {
                error = "Use one key plus modifiers, for example Ctrl+Alt+Space.";
                return false;
            }
        }

        if (key is null)
        {
            error = "Add a key after the modifiers, for example Ctrl+Alt+Space.";
            return false;
        }

        if ((mods & (HotkeyModifiers.Ctrl | HotkeyModifiers.Alt | HotkeyModifiers.Win)) == 0)
        {
            error = "Include Ctrl, Alt or Win so the hotkey doesn't clash with normal typing.";
            return false;
        }

        gesture = new HotkeyGesture(mods, char.ToUpperInvariant(key[0]) + key[1..]);
        return true;
    }

    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Win))
        {
            parts.Add("Win");
        }

        parts.Add(Key);
        return string.Join('+', parts);
    }

    private static HotkeyModifiers ParseModifier(string s) => s.ToUpperInvariant() switch
    {
        "CTRL" or "CONTROL" => HotkeyModifiers.Ctrl,
        "ALT" => HotkeyModifiers.Alt,
        "SHIFT" => HotkeyModifiers.Shift,
        "WIN" or "WINDOWS" or "META" => HotkeyModifiers.Win,
        _ => HotkeyModifiers.None,
    };
}
