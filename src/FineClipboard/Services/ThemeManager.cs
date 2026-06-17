using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace FineClipboard.Services;

/// <summary>
/// Applies a light/dark colour set as application resources. The popup references these
/// via DynamicResource, so changing them re-themes it live. (Settings stays system-default.)
/// </summary>
public static class ThemeManager
{
    public static void Apply(string? mode)
    {
        if (IsDark(mode))
        {
            Set("GlassPanelBrush", "#E5162231");
            Set("GlassSurfaceBrush", "#551E2B3C");
            Set("GlassSurfaceHoverBrush", "#70304A68");
            Set("GlassBorderBrush", "#35FFFFFF");
            Set("GlassBorderSubtleBrush", "#24FFFFFF");
            Set("PopupBackground", "#E5162231");
            Set("SurfaceBackground", "#541E2B3C");
            Set("SurfaceHover", "#70304A68");
            Set("SurfaceSelected", "#7A285A88");
            Set("TextPrimary", "#F1F6FF");
            Set("TextSecondary", "#A7B3C6");
            Set("BorderBrush", "#35FFFFFF");
            Set("AccentBrush", "#5CB8FF");
            Set("AccentSoftBrush", "#335CB8FF");
        }
        else
        {
            Set("GlassPanelBrush", "#EAF5F8FC");
            Set("GlassSurfaceBrush", "#BFFFFFFF");
            Set("GlassSurfaceHoverBrush", "#DDEAF4FF");
            Set("GlassBorderBrush", "#88FFFFFF");
            Set("GlassBorderSubtleBrush", "#330B1B2E");
            Set("PopupBackground", "#EAF5F8FC");
            Set("SurfaceBackground", "#AAFFFFFF");
            Set("SurfaceHover", "#DCEAF4FF");
            Set("SurfaceSelected", "#C7DCEBFF");
            Set("TextPrimary", "#172033");
            Set("TextSecondary", "#687489");
            Set("BorderBrush", "#78FFFFFF");
            Set("AccentBrush", "#408CF4");
            Set("AccentSoftBrush", "#263F8CFF");
        }
    }

    private static bool IsDark(string? mode)
    {
        if (mode == "dark") return true;
        if (mode == "light") return false;

        // "system" (or unset): follow the Windows app theme.
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int v)
            {
                return v == 0; // 0 = dark, 1 = light
            }
        }
        catch
        {
            // ignore — fall back to light
        }
        return false;
    }

    private static void Set(string key, string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        Application.Current.Resources[key] = brush;
    }
}
