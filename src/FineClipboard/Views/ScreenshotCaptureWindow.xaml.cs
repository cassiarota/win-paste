using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Bitmap = System.Drawing.Bitmap;
using CopyPixelOperation = System.Drawing.CopyPixelOperation;
using Graphics = System.Drawing.Graphics;
using ImageFormat = System.Drawing.Imaging.ImageFormat;
using PixelFormat = System.Drawing.Imaging.PixelFormat;
using Point = System.Windows.Point;

namespace FineClipboard.Views;

/// <summary>Pixel-accurate, Snipaste-style screen selector owned by FineClipboard.</summary>
public partial class ScreenshotCaptureWindow : Window
{
    private readonly int _originX;
    private readonly int _originY;
    private readonly Bitmap _screen;
    private readonly BitmapSource _source;
    private Point _start;
    private bool _dragging;
    private Rect _selection;
    private IntPtr _hwnd;
    private readonly Action? _canceled;
    private bool _accepted;

    public ScreenshotCaptureWindow(Action? canceled = null)
    {
        InitializeComponent();
        _canceled = canceled;
        System.Drawing.Rectangle virtualScreen = System.Windows.Forms.SystemInformation.VirtualScreen;
        _originX = virtualScreen.Left;
        _originY = virtualScreen.Top;
        _screen = new Bitmap(virtualScreen.Width, virtualScreen.Height, PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(_screen))
        {
            graphics.CopyFromScreen(virtualScreen.Left, virtualScreen.Top, 0, 0, virtualScreen.Size, CopyPixelOperation.SourceCopy);
        }
        _source = ToBitmapSource(_screen);
        ScreenImage.Source = _source;

        Left = virtualScreen.Left;
        Top = virtualScreen.Top;
        Width = virtualScreen.Width;
        Height = virtualScreen.Height;
        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            // WPF Width/Left are DIPs, while screen capture and Win32 window rectangles are
            // physical pixels. Native positioning keeps the overlay aligned at 125/150% DPI.
            SetWindowPos(_hwnd, new IntPtr(-1), _originX, _originY, _screen.Width, _screen.Height, 0x0040);
        };
        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            _screen.Dispose();
            if (!_accepted) _canceled?.Invoke();
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        Root.Width = ActualWidth;
        Root.Height = ActualHeight;
        ScreenImage.Width = ActualWidth;
        ScreenImage.Height = ActualHeight;
        Focus();
        Point point = Mouse.GetPosition(this);
        _selection = FindSnapRect(point);
        UpdatePointer(point);
        DrawSelection();
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        Point point = e.GetPosition(this);
        if (_dragging)
        {
            _selection = Normalize(_start, point);
        }
        else
        {
            _selection = FindSnapRect(point);
        }
        UpdatePointer(point);
        DrawSelection();
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _start = e.GetPosition(this);
        _dragging = true;
        CaptureMouse();
        e.Handled = true;
    }

    private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        Point end = e.GetPosition(this);
        ReleaseMouseCapture();
        _dragging = false;
        Rect manual = Normalize(_start, end);
        if (manual.Width >= 5 && manual.Height >= 5)
        {
            _selection = manual;
        }
        else
        {
            _selection = FindSnapRect(end);
        }
        AcceptSelection();
        e.Handled = true;
    }

    private void Window_MouseRightButtonDown(object sender, MouseButtonEventArgs e) => Close();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
        else if (e.Key == Key.Enter && _selection.Width > 0) AcceptSelection();
    }

    private void UpdatePointer(Point p)
    {
        CrossX.X1 = 0; CrossX.X2 = ActualWidth; CrossX.Y1 = CrossX.Y2 = p.Y;
        CrossY.Y1 = 0; CrossY.Y2 = ActualHeight; CrossY.X1 = CrossY.X2 = p.X;

        int px = Math.Clamp((int)Math.Round(p.X / Math.Max(1, ActualWidth) * _screen.Width), 0, _screen.Width - 1);
        int py = Math.Clamp((int)Math.Round(p.Y / Math.Max(1, ActualHeight) * _screen.Height), 0, _screen.Height - 1);
        System.Drawing.Color color = _screen.GetPixel(px, py);
        PixelInfo.Text = $"({_originX + px}, {_originY + py})  RGB({color.R}, {color.G}, {color.B})  #{color.R:X2}{color.G:X2}{color.B:X2}";

        int crop = 15;
        int cx = Math.Clamp(px - crop / 2, 0, Math.Max(0, _screen.Width - crop));
        int cy = Math.Clamp(py - crop / 2, 0, Math.Max(0, _screen.Height - crop));
        int cw = Math.Min(crop, _screen.Width - cx);
        int ch = Math.Min(crop, _screen.Height - cy);
        Magnifier.Source = new CroppedBitmap(_source, new Int32Rect(cx, cy, cw, ch));

        double ix = p.X + 18;
        double iy = p.Y + 18;
        if (ix + 155 > ActualWidth) ix = p.X - 165;
        if (iy + 135 > ActualHeight) iy = p.Y - 145;
        Canvas.SetLeft(Inspector, Math.Max(4, ix));
        Canvas.SetTop(Inspector, Math.Max(4, iy));
    }

    private Rect FindSnapRect(Point local)
    {
        int sx = _originX + (int)Math.Round(local.X / Math.Max(1, ActualWidth) * _screen.Width);
        int sy = _originY + (int)Math.Round(local.Y / Math.Max(1, ActualHeight) * _screen.Height);

        System.Drawing.Rectangle monitor = ScreenFromPoint(sx, sy);
        const int edge = 3;
        if (Math.Abs(sx - monitor.Left) <= edge || Math.Abs(sx - monitor.Right) <= edge ||
            Math.Abs(sy - monitor.Top) <= edge || Math.Abs(sy - monitor.Bottom) <= edge)
        {
            return FromScreenRect(monitor);
        }

        foreach (System.Drawing.Rectangle rect in VisibleWindows())
        {
            if (!rect.Contains(sx, sy)) continue;
            // Snipaste-style control snapping: the non-client title strip is its own useful
            // target; the client area snaps to the complete top-level window.
            int titleHeight = Math.Min(44, Math.Max(28, rect.Height / 8));
            if (sy < rect.Top + titleHeight)
            {
                return FromScreenRect(new System.Drawing.Rectangle(rect.Left, rect.Top, rect.Width, titleHeight));
            }
            return FromScreenRect(rect);
        }
        return FromScreenRect(monitor);
    }

    private Rect FromScreenRect(System.Drawing.Rectangle rect)
    {
        double scaleX = ActualWidth / _screen.Width;
        double scaleY = ActualHeight / _screen.Height;
        return new Rect((rect.Left - _originX) * scaleX, (rect.Top - _originY) * scaleY,
            rect.Width * scaleX, rect.Height * scaleY);
    }

    private void DrawSelection()
    {
        Rect r = Intersect(_selection, new Rect(0, 0, ActualWidth, ActualHeight));
        SetRect(SelectionBorder, r.X, r.Y, r.Width, r.Height);
        SetRect(ShadeTop, 0, 0, ActualWidth, r.Y);
        SetRect(ShadeLeft, 0, r.Y, r.X, r.Height);
        SetRect(ShadeRight, r.Right, r.Y, Math.Max(0, ActualWidth - r.Right), r.Height);
        SetRect(ShadeBottom, 0, r.Bottom, ActualWidth, Math.Max(0, ActualHeight - r.Bottom));
    }

    private void AcceptSelection()
    {
        Rect r = Intersect(_selection, new Rect(0, 0, ActualWidth, ActualHeight));
        if (r.Width < 2 || r.Height < 2) return;
        int x = Math.Clamp((int)Math.Round(r.X / ActualWidth * _screen.Width), 0, _screen.Width - 1);
        int y = Math.Clamp((int)Math.Round(r.Y / ActualHeight * _screen.Height), 0, _screen.Height - 1);
        int w = Math.Clamp((int)Math.Round(r.Width / ActualWidth * _screen.Width), 1, _screen.Width - x);
        int h = Math.Clamp((int)Math.Round(r.Height / ActualHeight * _screen.Height), 1, _screen.Height - y);
        using Bitmap selected = _screen.Clone(new System.Drawing.Rectangle(x, y, w, h), PixelFormat.Format32bppArgb);
        BitmapSource image = ToBitmapSource(selected);
        try { Clipboard.SetImage(image); } catch { return; }
        _accepted = true;
        Close();
    }

    private IEnumerable<System.Drawing.Rectangle> VisibleWindows()
    {
        var result = new List<System.Drawing.Rectangle>();
        uint ownPid = (uint)Process.GetCurrentProcess().Id;
        EnumWindows((window, _) =>
        {
            if (window == _hwnd || !IsWindowVisible(window) || IsIconic(window)) return true;
            GetWindowThreadProcessId(window, out uint pid);
            if (pid == ownPid) return true;
            if (DwmGetWindowAttribute(window, 9, out RECT rect, Marshal.SizeOf<RECT>()) != 0 && !GetWindowRect(window, out rect)) return true;
            var value = System.Drawing.Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
            if (value.Width > 10 && value.Height > 10) result.Add(value);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static System.Drawing.Rectangle ScreenFromPoint(int x, int y)
    {
        foreach (System.Windows.Forms.Screen screen in System.Windows.Forms.Screen.AllScreens)
            if (screen.Bounds.Contains(x, y)) return screen.Bounds;
        return System.Windows.Forms.Screen.PrimaryScreen?.Bounds ?? new System.Drawing.Rectangle(x, y, 1, 1);
    }

    private static Rect Normalize(Point a, Point b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
    private static Rect Intersect(Rect a, Rect b) { a.Intersect(b); return a.IsEmpty ? Rect.Empty : a; }
    private static void SetRect(FrameworkElement element, double x, double y, double width, double height)
    {
        Canvas.SetLeft(element, x); Canvas.SetTop(element, y);
        element.Width = Math.Max(0, width); element.Height = Math.Max(0, height);
    }

    private static BitmapSource ToBitmapSource(Bitmap bitmap)
    {
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        stream.Position = 0;
        var image = new BitmapImage();
        image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze();
        return image;
    }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out RECT value, int size);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
}
