namespace RLOverlay.Core.Config;

/// <summary>Modifier flags; values match Win32 <c>MOD_*</c> for <c>RegisterHotKey</c>.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x1,
    Control = 0x2,
    Shift = 0x4,
    Windows = 0x8,
}

/// <summary>
/// A global hotkey such as "Ctrl+R". <see cref="Key"/> is a WPF <c>System.Windows.Input.Key</c> name
/// (A–Z, D0–D9, F1–F24, NumPad0…, Oem keys). At least one modifier is required so plain typing is never stolen.
/// </summary>
public sealed record HotkeyGesture(HotkeyModifiers Modifiers, string Key)
{
    private static readonly Dictionary<string, HotkeyModifiers> ModifierNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = HotkeyModifiers.Control,
        ["control"] = HotkeyModifiers.Control,
        ["alt"] = HotkeyModifiers.Alt,
        ["shift"] = HotkeyModifiers.Shift,
        ["win"] = HotkeyModifiers.Windows,
        ["windows"] = HotkeyModifiers.Windows,
    };

    private static readonly HashSet<string> ModifierKeyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "LeftCtrl", "RightCtrl", "LeftAlt", "RightAlt", "LeftShift", "RightShift", "LWin", "RWin", "System",
    };

    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = new HotkeyGesture(HotkeyModifiers.None, "");
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        foreach (var part in parts[..^1])
        {
            if (!ModifierNames.TryGetValue(part, out var m))
            {
                return false;
            }

            modifiers |= m;
        }

        var key = NormalizeKey(parts[^1]);
        if (key is null)
        {
            return false;
        }

        gesture = new HotkeyGesture(modifiers, key);
        return true;
    }

    /// <summary>Builds a gesture from a key press in the settings window. Null if the key is only a modifier.</summary>
    public static HotkeyGesture? FromKeyPress(HotkeyModifiers modifiers, string keyName)
    {
        if (modifiers == HotkeyModifiers.None || ModifierKeyNames.Contains(keyName) || NormalizeKey(keyName) is not { } key)
        {
            return null;
        }

        return new HotkeyGesture(modifiers, key);
    }

    /// <summary>The parts shown as key caps in the panel header, e.g. ["Ctrl", "R"].</summary>
    public IReadOnlyList<string> DisplayParts
    {
        get
        {
            var parts = new List<string>();
            if (Modifiers.HasFlag(HotkeyModifiers.Control))
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

            if (Modifiers.HasFlag(HotkeyModifiers.Windows))
            {
                parts.Add("Win");
            }

            parts.Add(DisplayKey(Key));
            return parts;
        }
    }

    public override string ToString() => string.Join("+", DisplayParts);

    private static string? NormalizeKey(string raw)
    {
        var k = raw.Trim();
        if (k.Length == 0 || ModifierKeyNames.Contains(k))
        {
            return null;
        }

        if (k.Length == 1)
        {
            var c = char.ToUpperInvariant(k[0]);
            if (c is >= 'A' and <= 'Z')
            {
                return c.ToString();
            }

            if (c is >= '0' and <= '9')
            {
                return "D" + c;
            }

            return null;
        }

        if ((k[0] is 'F' or 'f') && int.TryParse(k[1..], out var f))
        {
            return f is >= 1 and <= 24 ? "F" + f : null;
        }

        if ((k[0] is 'D' or 'd') && k.Length == 2 && char.IsDigit(k[1]))
        {
            return "D" + k[1];
        }

        // Other WPF key names (NumPad0, Oem3, Home, Insert, ...) pass through; the app validates them against Key.
        return char.IsLetter(k[0]) && k.All(char.IsLetterOrDigit) ? char.ToUpperInvariant(k[0]) + k[1..] : null;
    }

    private static string DisplayKey(string key) =>
        key.Length == 2 && key[0] == 'D' && char.IsDigit(key[1]) ? key[1].ToString() : key;
}
