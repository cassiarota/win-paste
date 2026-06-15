using System.Collections.Generic;
using System.Windows.Input;
using PasteNowWin.Interop;

namespace PasteNowWin.Services;

/// <summary>A global-hotkey combination: Win32 modifier flags + virtual-key code.</summary>
public readonly record struct HotkeyCombo(uint Modifiers, uint VirtualKey)
{
    public string Serialize() => $"{Modifiers}:{VirtualKey}";

    public static HotkeyCombo Parse(string? serialized, HotkeyCombo fallback)
    {
        if (!string.IsNullOrWhiteSpace(serialized))
        {
            string[] parts = serialized.Split(':');
            if (parts.Length == 2 &&
                uint.TryParse(parts[0], out uint mods) &&
                uint.TryParse(parts[1], out uint vk) &&
                vk != 0)
            {
                return new HotkeyCombo(mods, vk);
            }
        }
        return fallback;
    }

    /// <summary>Human-readable form, e.g. "Ctrl + Shift + V".</summary>
    public string ToDisplayString()
    {
        var parts = new List<string>(4);
        if ((Modifiers & NativeMethods.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((Modifiers & NativeMethods.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((Modifiers & NativeMethods.MOD_ALT) != 0) parts.Add("Alt");
        if ((Modifiers & NativeMethods.MOD_WIN) != 0) parts.Add("Win");
        parts.Add(KeyName(VirtualKey));
        return string.Join(" + ", parts);
    }

    private static string KeyName(uint vk)
    {
        string s = KeyInterop.KeyFromVirtualKey((int)vk).ToString();
        if (s.Length == 2 && s[0] == 'D' && char.IsDigit(s[1]))
        {
            return s[1].ToString(); // D1 -> 1
        }
        return s;
    }
}
