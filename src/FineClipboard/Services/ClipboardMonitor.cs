using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media.Imaging;
using FineClipboard.Interop;
using FineClipboard.Models;
using WpfClipboard = System.Windows.Clipboard;
using WpfTextDataFormat = System.Windows.TextDataFormat;

namespace FineClipboard.Services;

/// <summary>
/// Listens for OS clipboard changes and turns them into <see cref="ClipboardItem"/>s.
/// Reads run on the UI/dispatcher thread (required for the WPF Clipboard API).
/// </summary>
public sealed class ClipboardMonitor : IDisposable
{
    private const int PreviewLength = 400;

    private readonly NativeMessageWindow _msg;
    private long _suppressUntilTick;

    /// <summary>Raised when new clipboard content is captured.</summary>
    public event Action<ClipboardItem>? ItemCaptured;

    /// <summary>
    /// Optional predicate: given the source process name, return true to skip capture
    /// entirely (used by exclusion rules so sensitive content is never even read).
    /// </summary>
    public Func<string?, bool>? ShouldSkipSource { get; set; }

    /// <summary>When true, clipboard changes are ignored (privacy / pause mode).</summary>
    public bool Paused { get; set; }

    public ClipboardMonitor(NativeMessageWindow msg)
    {
        _msg = msg;
        NativeMethods.AddClipboardFormatListener(_msg.Handle);
        _msg.MessageReceived += OnMessage;
    }

    /// <summary>
    /// Suppresses capture for <paramref name="ms"/> milliseconds. Used while the app
    /// itself writes to the clipboard (during paste) so we don't re-record our own writes.
    /// </summary>
    public void SuppressFor(int ms) => _suppressUntilTick = Environment.TickCount64 + ms;

    private void OnMessage(int msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg != NativeMethods.WM_CLIPBOARDUPDATE)
        {
            return;
        }
        if (Paused || Environment.TickCount64 < _suppressUntilTick)
        {
            return;
        }

        ClipboardItem? item = ReadClipboard();
        if (item != null)
        {
            ItemCaptured?.Invoke(item);
        }
    }

    private ClipboardItem? ReadClipboard()
    {
        // The clipboard may briefly be locked by the app that just wrote it; retry a few times.
        for (int attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                string? source = GetForegroundProcessName();

                // Exclusion rules: skip before reading any clipboard content.
                if (ShouldSkipSource?.Invoke(source) == true)
                {
                    return null;
                }

                if (WpfClipboard.ContainsImage())
                {
                    BitmapSource? img = WpfClipboard.GetImage();
                    if (img != null)
                    {
                        return new ClipboardItem
                        {
                            Type = ClipItemType.Image,
                            ImageData = EncodePng(img),
                            Preview = $"图片 {img.PixelWidth}×{img.PixelHeight}",
                            SourceApp = source,
                            CreatedAt = DateTime.UtcNow,
                        };
                    }
                }

                if (WpfClipboard.ContainsFileDropList())
                {
                    var files = WpfClipboard.GetFileDropList().Cast<string>().Where(p => !string.IsNullOrEmpty(p)).ToList();
                    if (files.Count > 0)
                    {
                        string preview = files.Count == 1
                            ? Path.GetFileName(files[0])
                            : $"{files.Count} 个文件: " + string.Join(", ", files.Take(3).Select(Path.GetFileName));
                        return new ClipboardItem
                        {
                            Type = ClipItemType.Files,
                            Text = string.Join("\n", files),
                            Preview = preview,
                            SourceApp = source,
                            CreatedAt = DateTime.UtcNow,
                        };
                    }
                }

                if (WpfClipboard.ContainsText())
                {
                    string text = WpfClipboard.GetText();
                    if (!string.IsNullOrEmpty(text))
                    {
                        return new ClipboardItem
                        {
                            Type = ClipItemType.Text,
                            Text = text,
                            Html = TryGetFormat(WpfTextDataFormat.Html),
                            Rtf = TryGetFormat(WpfTextDataFormat.Rtf),
                            Preview = MakePreview(text),
                            SourceApp = source,
                            CreatedAt = DateTime.UtcNow,
                        };
                    }
                }

                return null;
            }
            catch (COMException)
            {
                Thread.Sleep(40);
            }
            catch (Exception)
            {
                return null;
            }
        }
        return null;
    }

    private static string MakePreview(string text)
    {
        string trimmed = text.Trim();
        return trimmed.Length > PreviewLength ? trimmed[..PreviewLength] : trimmed;
    }

    /// <summary>Best-effort read of an additional text format (HTML/RTF) from the clipboard.</summary>
    private static string? TryGetFormat(WpfTextDataFormat format)
    {
        try
        {
            if (WpfClipboard.ContainsText(format))
            {
                string s = WpfClipboard.GetText(format);
                return string.IsNullOrEmpty(s) ? null : s;
            }
        }
        catch
        {
            // Some sources expose a format header without readable content — ignore.
        }
        return null;
    }

    private static byte[] EncodePng(BitmapSource source)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private static string? GetForegroundProcessName()
    {
        try
        {
            IntPtr hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero)
            {
                return null;
            }
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0)
            {
                return null;
            }
            using Process p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        NativeMethods.RemoveClipboardFormatListener(_msg.Handle);
        _msg.MessageReceived -= OnMessage;
    }
}
