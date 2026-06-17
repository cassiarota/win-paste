using SD = System.Drawing;

namespace FineClipboard.Services;

/// <summary>
/// Builds the tray icon at runtime so the project ships without a binary .ico asset.
/// </summary>
internal static class TrayIconFactory
{
    public static SD.Icon Create() => AppIconFactory.CreateIcon();
}
