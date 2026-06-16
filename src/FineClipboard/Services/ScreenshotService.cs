using System.Diagnostics;

namespace FineClipboard.Services;

/// <summary>
/// Screen capture. The interactive mode opens the built-in Snip overlay (ms-screenclip),
/// which supports rectangle, magnetic-window, and fullscreen selection and copies the result
/// to the clipboard. Fullscreen is also available directly (all monitors). Either way the
/// captured image lands on the clipboard and flows into history like any copy — so it is
/// OCR'd and can be saved to a file via the image right-click menu.
/// </summary>
public static class ScreenshotService
{
    /// <summary>Opens the system Snip overlay (rectangle / window / fullscreen → clipboard).</summary>
    public static void CaptureInteractive()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-screenclip:") { UseShellExecute = true });
        }
        catch
        {
            try
            {
                Process.Start(new ProcessStartInfo("snippingtool.exe") { UseShellExecute = true });
            }
            catch
            {
                // No snip tool available — nothing to do.
            }
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
