using System;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace FineClipboard.Services;

/// <summary>
/// On-device text recognition for captured images, using the built-in Windows OCR engine
/// (<c>Windows.Media.Ocr</c>). No network, no third-party dependency. Recognized text is
/// stored alongside the image so screenshots become searchable. Returns null when no OCR
/// language pack is installed or recognition yields nothing.
/// </summary>
public static class OcrService
{
    /// <summary>True if at least one OCR language pack is available on this machine.</summary>
    public static bool IsAvailable
    {
        get
        {
            try
            {
                return OcrEngine.TryCreateFromUserProfileLanguages() != null;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Recognizes text in a PNG image. Never throws; returns null on failure / empty.</summary>
    public static async Task<string?> RecognizeAsync(byte[] png)
    {
        if (png == null || png.Length == 0)
        {
            return null;
        }
        try
        {
            OcrEngine? engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine == null)
            {
                return null;
            }

            using var stream = new InMemoryRandomAccessStream();
            var writer = new DataWriter(stream);
            writer.WriteBytes(png);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
            stream.Seek(0);

            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
            using SoftwareBitmap raw = await decoder.GetSoftwareBitmapAsync();
            using SoftwareBitmap bmp = SoftwareBitmap.Convert(raw, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

            OcrResult result = await engine.RecognizeAsync(bmp);
            string text = result?.Text?.Trim() ?? string.Empty;
            return text.Length == 0 ? null : text;
        }
        catch
        {
            return null;
        }
    }
}
