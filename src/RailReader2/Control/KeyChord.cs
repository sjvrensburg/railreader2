using Avalonia.Input;

namespace RailReader2.ControlBus;

/// <summary>
/// Parses a human key-chord string (e.g. "c", "f11", "ctrl+shift+h", "plus", "right") into an
/// Avalonia <see cref="Key"/> + <see cref="KeyModifiers"/>, so the demo DSL's generic <c>key</c>
/// verb can drive any keyboard shortcut. Modifier tokens: ctrl/control, shift, alt, meta/super/win/cmd.
/// </summary>
public static class KeyChord
{
    public static bool TryParse(string? chord, out Key key, out KeyModifiers mods)
    {
        key = Key.None;
        mods = KeyModifiers.None;
        if (string.IsNullOrWhiteSpace(chord)) return false;

        string s = chord.Trim();
        if (s == "+") return (key = Key.OemPlus) != Key.None; // the lone '+' key

        var tokens = s.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) return false;

        for (int i = 0; i < tokens.Length - 1; i++)
        {
            switch (tokens[i].ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= KeyModifiers.Control; break;
                case "shift": mods |= KeyModifiers.Shift; break;
                case "alt": mods |= KeyModifiers.Alt; break;
                case "meta" or "super" or "win" or "cmd": mods |= KeyModifiers.Meta; break;
                default: return false; // unknown modifier
            }
        }

        return TryParseKey(tokens[^1], out key);
    }

    private static bool TryParseKey(string token, out Key key)
    {
        string t = token.ToLowerInvariant();
        key = t switch
        {
            "plus" or "equals" or "=" => Key.OemPlus,
            "minus" or "-" => Key.OemMinus,
            "comma" or "," => Key.OemComma,
            "tilde" or "backtick" or "`" => Key.OemTilde,
            "openbracket" or "[" => Key.OemOpenBrackets,
            "closebracket" or "]" => Key.OemCloseBrackets,
            "space" => Key.Space,
            "enter" or "return" => Key.Enter,
            "esc" or "escape" => Key.Escape,
            "tab" => Key.Tab,
            "del" or "delete" => Key.Delete,
            "backspace" => Key.Back,
            "pageup" or "pgup" => Key.PageUp,
            "pagedown" or "pgdn" => Key.PageDown,
            "home" => Key.Home,
            "end" => Key.End,
            "left" => Key.Left,
            "right" => Key.Right,
            "up" => Key.Up,
            "down" => Key.Down,
            _ => Key.None,
        };
        if (key != Key.None) return true;

        // a-z, 0-9 → Key.A.., Key.D0..; otherwise fall back to the enum name (F1, NumPad0, …).
        if (t.Length == 1 && t[0] is >= 'a' and <= 'z') { key = Key.A + (t[0] - 'a'); return true; }
        if (t.Length == 1 && t[0] is >= '0' and <= '9') { key = Key.D0 + (t[0] - '0'); return true; }
        return Enum.TryParse(token, ignoreCase: true, out key) && key != Key.None;
    }
}
