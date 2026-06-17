using System;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using FineClipboard.Services;
using Microsoft.Win32;

namespace FineClipboard.Views;

public partial class ScreenshotPreviewWindow : Window
{
    private readonly byte[] _png;

    public ScreenshotPreviewWindow(byte[] png)
    {
        InitializeComponent();
        _png = png;
        Icon = AppIconFactory.CreateImageSource();
        PreviewImage.Source = LoadBitmap(png);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "PNG 图片|*.png",
            FileName = $"screenshot-{DateTime.Now:yyyyMMdd-HHmmss}.png",
        };
        if (dialog.ShowDialog(this) == true)
        {
            File.WriteAllBytes(dialog.FileName, _png);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static BitmapImage LoadBitmap(byte[] png)
    {
        var bmp = new BitmapImage();
        using var ms = new MemoryStream(png);
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = ms;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}
