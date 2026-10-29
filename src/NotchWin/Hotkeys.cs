using System.Windows.Input;

namespace NotchWin;

/// <summary>Сочетания клавиш в виде строки «Ctrl+Alt+T»: разбор для RegisterHotKey и запись из нажатия.</summary>
internal static class Hotkeys
{
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    /// <summary>Модификаторы и код клавиши для RegisterHotKey. false — строка пустая или без обычной клавиши.</summary>
    public static bool TryParse(string combo, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        foreach (var part in combo.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= MOD_CONTROL; break;
                case "alt": modifiers |= MOD_ALT; break;
                case "shift": modifiers |= MOD_SHIFT; break;
                case "win": modifiers |= MOD_WIN; break;
                default:
                    if (!Enum.TryParse<Key>(part, true, out var key) || int.TryParse(part, out _)) return false;
                    vk = (uint)KeyInterop.VirtualKeyFromKey(key);
                    break;
            }
        }
        return vk != 0;
    }

    /// <summary>Сочетание из нажатия в окне настроек. null — нажали только модификатор, ждём основную клавишу.</summary>
    public static string? FromKeyEvent(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin or Key.None or Key.ImeProcessed or Key.DeadCharProcessed)
            return null;
        var parts = new List<string>();
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Windows) || Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    /// <summary>Как показать сочетание человеку: «Alt + Пробел», «Ctrl + Alt + T».</summary>
    public static string Display(string combo)
    {
        if (string.IsNullOrWhiteSpace(combo)) return "Нет";
        return string.Join(" + ", combo.Split('+', StringSplitOptions.TrimEntries).Select(p => p switch
        {
            "Space" => "Пробел",
            "Return" or "Enter" => "Enter",
            "Escape" => "Esc",
            "Back" => "Backspace",
            "OemTilde" => "Ё",
            "OemMinus" => "-",
            "OemPlus" => "=",
            "OemPeriod" => ".",
            "OemComma" => ",",
            _ when p.Length == 2 && p[0] == 'D' && char.IsDigit(p[1]) => p[1..],
            _ when p.StartsWith("NumPad") => "Num " + p[6..],
            _ => p,
        }));
    }
}
