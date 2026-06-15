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
}
