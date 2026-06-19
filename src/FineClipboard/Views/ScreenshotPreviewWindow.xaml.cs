using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using FineClipboard.Services;
using Microsoft.Win32;

namespace FineClipboard.Views;

/// <summary>Non-destructive screenshot annotation surface with PNG export/copy.</summary>
public partial class ScreenshotPreviewWindow : Window
{
    private readonly BitmapSource _bitmap;
    private readonly BitmapSource _pixelBitmap;
    private readonly List<UIElement> _operations = new();
    private string _tool = "select";
    private Point _start;
    private UIElement? _active;
    private bool _drawing;

    public ScreenshotPreviewWindow(byte[] png)
    {
        InitializeComponent();
        Icon = AppIconFactory.CreateImageSource();
        _bitmap = LoadBitmap(png);
        _pixelBitmap = new FormatConvertedBitmap(_bitmap, PixelFormats.Bgra32, null, 0);
        PreviewImage.Source = _bitmap;
        EditorSurface.Width = _bitmap.PixelWidth;
        EditorSurface.Height = _bitmap.PixelHeight;
        AnnotationCanvas.Width = _bitmap.PixelWidth;
        AnnotationCanvas.Height = _bitmap.PixelHeight;
        SelectTool.IsChecked = true;
    }

    private void Tool_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton selected || selected.Tag is not string tool) return;
        _tool = tool;
        foreach (ToggleButton button in new[] { SelectTool, RectTool, ArrowTool, PenTool, MosaicTool, TextTool })
            if (!ReferenceEquals(button, selected)) button.IsChecked = false;
        selected.IsChecked = true;
        AnnotationCanvas.Cursor = tool == "select" ? Cursors.Arrow : tool == "text" ? Cursors.IBeam : Cursors.Cross;
    }

    private void Editor_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Point p = Clamp(e.GetPosition(AnnotationCanvas));
        if (_tool == "select") return;
        if (_tool == "text")
        {
            AddTextEditor(p);
            e.Handled = true;
            return;
        }

        _start = p;
        _drawing = true;
        AnnotationCanvas.CaptureMouse();
        _active = _tool switch
        {
            "rect" => new Rectangle { Stroke = Brushes.Red, StrokeThickness = 3, Fill = Brushes.Transparent },
            "arrow" => new Canvas { Width = _bitmap.PixelWidth, Height = _bitmap.PixelHeight },
            "pen" => new Polyline { Stroke = Brushes.Red, StrokeThickness = 4, StrokeLineJoin = PenLineJoin.Round },
            "mosaic" => new Canvas { Width = _bitmap.PixelWidth, Height = _bitmap.PixelHeight },
            _ => null,
        };
        if (_active == null) return;
        AnnotationCanvas.Children.Add(_active);
        _operations.Add(_active);
        if (_active is Polyline polyline) polyline.Points.Add(p);
        if (_tool == "mosaic") AddMosaicBlock((Canvas)_active, p);
        e.Handled = true;
    }

    private void Editor_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_drawing || _active == null || e.LeftButton != MouseButtonState.Pressed) return;
        Point p = Clamp(e.GetPosition(AnnotationCanvas));
        if (_active is Rectangle rect)
        {
            Canvas.SetLeft(rect, Math.Min(_start.X, p.X)); Canvas.SetTop(rect, Math.Min(_start.Y, p.Y));
            rect.Width = Math.Abs(p.X - _start.X); rect.Height = Math.Abs(p.Y - _start.Y);
        }
        else if (_tool == "arrow" && _active is Canvas arrow)
        {
            DrawArrow(arrow, _start, p);
        }
        else if (_active is Polyline line)
        {
            line.Points.Add(p);
        }
        else if (_tool == "mosaic" && _active is Canvas mosaic)
        {
            AddMosaicBlock(mosaic, p);
        }
    }

    private void Editor_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_drawing) return;
        _drawing = false;
        _active = null;
        AnnotationCanvas.ReleaseMouseCapture();
        e.Handled = true;
    }

    private void AddTextEditor(Point point)
    {
        var box = new TextBox
        {
            Text = "输入文字", Foreground = Brushes.Red, Background = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)),
            BorderBrush = Brushes.Red, BorderThickness = new Thickness(1), FontSize = Math.Max(18, _bitmap.PixelWidth / 60.0),
            MinWidth = 100, Padding = new Thickness(3), AcceptsReturn = true,
        };
        Canvas.SetLeft(box, point.X); Canvas.SetTop(box, point.Y);
        AnnotationCanvas.Children.Add(box); _operations.Add(box);
        box.Focus(); box.SelectAll();
    }

    private void AddMosaicBlock(Canvas layer, Point p)
    {
        const int size = 18;
        int x = Math.Clamp((int)p.X / size * size, 0, Math.Max(0, _bitmap.PixelWidth - size));
        int y = Math.Clamp((int)p.Y / size * size, 0, Math.Max(0, _bitmap.PixelHeight - size));
        if (layer.Children.Cast<FrameworkElement>().Any(v => (int)Canvas.GetLeft(v) == x && (int)Canvas.GetTop(v) == y)) return;
        int w = Math.Min(size, _bitmap.PixelWidth - x), h = Math.Min(size, _bitmap.PixelHeight - y);
        var block = new Rectangle { Fill = new SolidColorBrush(SampleColor(x + w / 2, y + h / 2)), Width = w, Height = h };
        Canvas.SetLeft(block, x); Canvas.SetTop(block, y); layer.Children.Add(block);
    }

    private Color SampleColor(int x, int y)
    {
        var pixel = new byte[4];
        _pixelBitmap.CopyPixels(new Int32Rect(Math.Clamp(x, 0, _bitmap.PixelWidth - 1), Math.Clamp(y, 0, _bitmap.PixelHeight - 1), 1, 1), pixel, 4, 0);
        return Color.FromArgb(255, pixel[2], pixel[1], pixel[0]);
    }

    private static void DrawArrow(Canvas canvas, Point start, Point end)
    {
        canvas.Children.Clear();
        var shaft = new Line { X1 = start.X, Y1 = start.Y, X2 = end.X, Y2 = end.Y, Stroke = Brushes.Red, StrokeThickness = 4 };
        double angle = Math.Atan2(end.Y - start.Y, end.X - start.X);
        const double length = 20, spread = 0.55;
        var head = new Polygon { Fill = Brushes.Red, Points = new PointCollection
        {
            end,
            new(end.X - length * Math.Cos(angle - spread), end.Y - length * Math.Sin(angle - spread)),
            new(end.X - length * Math.Cos(angle + spread), end.Y - length * Math.Sin(angle + spread)),
        }};
        canvas.Children.Add(shaft); canvas.Children.Add(head);
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (_operations.Count == 0) return;
        UIElement item = _operations[^1]; _operations.RemoveAt(_operations.Count - 1); AnnotationCanvas.Children.Remove(item);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        BitmapSource result = Render();
        try { Clipboard.SetImage(result); Hint.Text = "已复制标注后的截图"; } catch { Hint.Text = "复制失败：剪贴板正被其他程序占用"; }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "PNG 图片|*.png", FileName = $"screenshot-{DateTime.Now:yyyyMMdd-HHmmss}.png" };
        if (dialog.ShowDialog(this) != true) return;
        using FileStream file = File.Create(dialog.FileName);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(Render())); encoder.Save(file);
        Hint.Text = $"已导出到 {dialog.FileName}";
    }

    private void Done_Click(object sender, RoutedEventArgs e) { Copy_Click(sender, e); Close(); }

    private BitmapSource Render()
    {
        Keyboard.ClearFocus();
        EditorSurface.UpdateLayout();
        var result = new RenderTargetBitmap(_bitmap.PixelWidth, _bitmap.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        result.Render(EditorSurface); result.Freeze(); return result;
    }

    private Point Clamp(Point p) => new(Math.Clamp(p.X, 0, _bitmap.PixelWidth), Math.Clamp(p.Y, 0, _bitmap.PixelHeight));

    private static BitmapImage LoadBitmap(byte[] png)
    {
        var bmp = new BitmapImage(); using var ms = new MemoryStream(png);
        bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.StreamSource = ms; bmp.EndInit(); bmp.Freeze(); return bmp;
    }
}
