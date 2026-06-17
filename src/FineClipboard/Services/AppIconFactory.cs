using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SD = System.Drawing;

namespace FineClipboard.Services;

internal static class AppIconFactory
{
    public static SD.Icon CreateIcon(int size = 32)
    {
        using SD.Bitmap bmp = DrawBitmap(size);
        IntPtr hIcon = bmp.GetHicon();
        try
        {
            using SD.Icon temp = SD.Icon.FromHandle(hIcon);
            return (SD.Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    public static ImageSource CreateImageSource(int size = 32)
    {
        using SD.Bitmap bmp = DrawBitmap(size);
        IntPtr hIcon = bmp.GetHicon();
        try
        {
            BitmapSource source = Imaging.CreateBitmapSourceFromHIcon(
                hIcon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(size, size));
            source.Freeze();
            return source;
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static SD.Bitmap DrawBitmap(int size)
    {
        var bmp = new SD.Bitmap(size, size, SD.Imaging.PixelFormat.Format32bppArgb);
        using SD.Graphics g = SD.Graphics.FromImage(bmp);
        g.SmoothingMode = SD.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(SD.Color.Transparent);

        float scale = size / 32f;
        using var baseBrush = new SD.Drawing2D.LinearGradientBrush(
            new SD.RectangleF(2 * scale, 2 * scale, 28 * scale, 28 * scale),
            SD.Color.FromArgb(245, 28, 39, 55),
            SD.Color.FromArgb(235, 9, 15, 26),
            45f);
        FillRoundRect(g, baseBrush, 2 * scale, 2 * scale, 28 * scale, 28 * scale, 8 * scale);

        using var rim = new SD.Pen(SD.Color.FromArgb(110, 255, 255, 255), 1.1f * scale);
        DrawRoundRect(g, rim, 2.5f * scale, 2.5f * scale, 27 * scale, 27 * scale, 8 * scale);

        using var glow = new SD.Pen(SD.Color.FromArgb(190, 98, 202, 255), 1.6f * scale)
        {
            StartCap = SD.Drawing2D.LineCap.Round,
            EndCap = SD.Drawing2D.LineCap.Round,
        };
        g.DrawArc(glow, 7 * scale, 7 * scale, 18 * scale, 18 * scale, 210, 210);

        using var paper = new SD.SolidBrush(SD.Color.FromArgb(238, 236, 245, 255));
        FillRoundRect(g, paper, 10 * scale, 8 * scale, 13 * scale, 17 * scale, 3 * scale);

        using var clip = new SD.Pen(SD.Color.FromArgb(235, 64, 153, 255), 2.2f * scale)
        {
            StartCap = SD.Drawing2D.LineCap.Round,
            EndCap = SD.Drawing2D.LineCap.Round,
        };
        g.DrawLine(clip, 13 * scale, 8 * scale, 20 * scale, 8 * scale);
        g.DrawLine(clip, 13 * scale, 13 * scale, 20 * scale, 13 * scale);
        g.DrawLine(clip, 13 * scale, 18 * scale, 18 * scale, 18 * scale);

        using var spark = new SD.Pen(SD.Color.FromArgb(245, 160, 227, 255), 1.4f * scale)
        {
            StartCap = SD.Drawing2D.LineCap.Round,
            EndCap = SD.Drawing2D.LineCap.Round,
        };
        g.DrawLine(spark, 23.5f * scale, 7 * scale, 23.5f * scale, 11 * scale);
        g.DrawLine(spark, 21.5f * scale, 9 * scale, 25.5f * scale, 9 * scale);

        return bmp;
    }

    private static void FillRoundRect(SD.Graphics g, SD.Brush brush, float x, float y, float w, float h, float r)
    {
        using SD.Drawing2D.GraphicsPath path = RoundRect(x, y, w, h, r);
        g.FillPath(brush, path);
    }

    private static void DrawRoundRect(SD.Graphics g, SD.Pen pen, float x, float y, float w, float h, float r)
    {
        using SD.Drawing2D.GraphicsPath path = RoundRect(x, y, w, h, r);
        g.DrawPath(pen, path);
    }

    private static SD.Drawing2D.GraphicsPath RoundRect(float x, float y, float w, float h, float r)
    {
        float d = r * 2;
        var path = new SD.Drawing2D.GraphicsPath();
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
