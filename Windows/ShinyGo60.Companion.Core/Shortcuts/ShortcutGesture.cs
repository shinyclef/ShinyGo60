using System.Globalization;

namespace ShinyGo60.Companion.Core.Shortcuts;

public sealed record ShortcutGesture
{
    private static readonly Dictionary<string, string> NamedKeys =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["BACKSPACE"] = "Backspace",
            ["DELETE"] = "Delete",
            ["END"] = "End",
            ["ENTER"] = "Enter",
            ["ESC"] = "Escape",
            ["ESCAPE"] = "Escape",
            ["HOME"] = "Home",
            ["INSERT"] = "Insert",
            ["PAGEDOWN"] = "PageDown",
            ["PAGEUP"] = "PageUp",
            ["SPACE"] = "Space",
            ["TAB"] = "Tab",
        };

    private ShortcutGesture(ShortcutModifiers modifiers, string key)
    {
        this.Modifiers = modifiers;
        this.Key = key;
    }

    public ShortcutModifiers Modifiers { get; }

    public string Key { get; }

    public static ShortcutGesture Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string[] parts = value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            throw new FormatException("A shortcut must contain a key.");
        }

        ShortcutModifiers modifiers = ShortcutModifiers.None;
        string? key = null;
        foreach (string part in parts)
        {
            if (TryParseModifier(part, out ShortcutModifiers modifier))
            {
                if ((modifiers & modifier) != 0)
                {
                    throw new FormatException($"Shortcut modifier '{part}' is duplicated.");
                }

                modifiers |= modifier;
                continue;
            }

            if (key is not null)
            {
                throw new FormatException("A shortcut must contain exactly one non-modifier key.");
            }

            key = NormalizeKey(part);
        }

        return key is null
            ? throw new FormatException("A shortcut must contain one non-modifier key.")
            : new ShortcutGesture(modifiers, key);
    }

    public static ShortcutGesture FromInput(string key, ShortcutModifiers modifiers)
    {
        const ShortcutModifiers knownModifiers =
            ShortcutModifiers.Control | ShortcutModifiers.Alt | ShortcutModifiers.Shift | ShortcutModifiers.Windows;
        if ((modifiers & ~knownModifiers) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(modifiers), $"Unknown shortcut modifiers: {modifiers}.");
        }

        return new ShortcutGesture(modifiers, NormalizeKey(key));
    }

    public static string NormalizeKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        string key = value.Trim();
        if (key.Length == 1 && char.IsAsciiLetterOrDigit(key[0]))
        {
            return key.ToUpperInvariant();
        }

        if (key.Length is 2 or 3 && (key[0] is 'F' or 'f') &&
            int.TryParse(key.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int functionKey) &&
            functionKey is >= 1 and <= 24)
        {
            return $"F{functionKey.ToString(CultureInfo.InvariantCulture)}";
        }

        string compactKey = key.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (NamedKeys.TryGetValue(compactKey, out string? namedKey))
        {
            return namedKey;
        }

        throw new FormatException(
            $"Shortcut key '{value}' is unsupported. Use A-Z, 0-9, F1-F24, or a supported navigation key.");
    }

    public override string ToString()
    {
        List<string> parts = [];
        AddModifier(parts, this.Modifiers, ShortcutModifiers.Control, "Ctrl");
        AddModifier(parts, this.Modifiers, ShortcutModifiers.Alt, "Alt");
        AddModifier(parts, this.Modifiers, ShortcutModifiers.Shift, "Shift");
        AddModifier(parts, this.Modifiers, ShortcutModifiers.Windows, "Win");
        parts.Add(this.Key);
        return string.Join('+', parts);
    }

    private static void AddModifier(
        List<string> parts,
        ShortcutModifiers actual,
        ShortcutModifiers expected,
        string name)
    {
        if ((actual & expected) != 0)
        {
            parts.Add(name);
        }
    }

    private static bool TryParseModifier(string value, out ShortcutModifiers modifier)
    {
        modifier = value.ToUpperInvariant() switch
        {
            "CTRL" or "CONTROL" => ShortcutModifiers.Control,
            "ALT" => ShortcutModifiers.Alt,
            "SHIFT" => ShortcutModifiers.Shift,
            "WIN" or "WINDOWS" => ShortcutModifiers.Windows,
            _ => ShortcutModifiers.None,
        };
        return modifier != ShortcutModifiers.None;
    }
}
