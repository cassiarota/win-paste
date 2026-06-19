using System;
using FineClipboard.Views;

namespace FineClipboard.Services;

/// <summary>
/// Screen capture. The interactive mode uses FineClipboard's pixel-accurate overlay with
/// window/screen snapping, crosshairs, magnifier, coordinates and RGB sampling. Either way the
/// captured image lands on the clipboard and flows into history like any copy — so it is
/// OCR'd and can be saved to a file via the image right-click menu.
/// </summary>
public static class ScreenshotService
{
    /// <summary>Opens the FineClipboard smart-selection overlay.</summary>
    public static void CaptureInteractive(Action? canceled = null)
    {
        try
        {
            new ScreenshotCaptureWindow(canceled).Show();
        }
        catch
        {
            // A secure desktop or unavailable screen can prevent capture.
            canceled?.Invoke();
        }
    }

    /// <summary>Captures the whole virtual screen (all monitors) directly to the clipboard.</summary>
    public static void CaptureFullscreen()
    {
        try
        {
            System.Drawing.Rectangle bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
            using var bmp = new System.Drawing.Bitmap(bounds.Width, bounds.Height);
            using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size);
            }
            System.Windows.Forms.Clipboard.SetImage(bmp);
        }
        catch
        {
            // Best effort: a locked clipboard / no screen simply skips the capture.
        }
    }
}
