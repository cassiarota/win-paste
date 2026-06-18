using System;
using System.Collections.Specialized;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using FineClipboard.Interop;
using FineClipboard.Models;
using WpfClipboard = System.Windows.Clipboard;
using WpfTextDataFormat = System.Windows.TextDataFormat;

namespace FineClipboard.Services;

/// <summary>
/// Writes a history item to the clipboard, restores focus to the target window,
/// and injects Ctrl+V so the content lands in the app the user was using.
/// </summary>
public sealed class PasteService
{
    private readonly ClipboardMonitor _monitor;
    private readonly HistoryStore _store;

    public PasteService(ClipboardMonitor monitor, HistoryStore store)
    {
        _monitor = monitor;
        _store = store;
    }

    /// <summary>Pastes a specific item into <paramref name="target"/> (the previously focused window).</summary>
    public async Task PasteItemAsync(ClipboardItem item, IntPtr target, bool plainText)
    {
        SetClipboard(item, plainText);
        await Task.Delay(60).ConfigureAwait(true);

        NativeMethods.ForceForeground(target);
        await Task.Delay(90).ConfigureAwait(true);

        NativeMethods.SendCtrlV();
        _store.Touch(item.Id);
    }

    /// <summary>Pastes the most recent text item as plain text into the current foreground window.</summary>
    public async Task PasteRecentPlainAsync()
    {
        ClipboardItem? item = _store.GetMostRecentText();
        if (item == null || string.IsNullOrEmpty(item.Text))
        {
            return;
        }

        // No popup was shown, so the foreground window is still the user's target.
        _monitor.SuppressFor(600);
        SetText(item.Text);
        await WaitForShortcutReleaseAsync().ConfigureAwait(true);

        NativeMethods.SendCtrlV();
        _store.Touch(item.Id);
    }

    private static async Task WaitForShortcutReleaseAsync()
    {
        // The plain-paste hotkey defaults to Ctrl+Shift+B. If Shift is still physically down
        // when Ctrl+V is injected, Windows sees Ctrl+Shift+V and opens the history popup again.
        for (int attempt = 0; attempt < 150 && NativeMethods.AreShortcutModifiersPressed(); attempt++)
        {
            await Task.Delay(10).ConfigureAwait(true);
        }
        await Task.Delay(20).ConfigureAwait(true);
    }

    /// <summary>Puts an item on the clipboard without pasting (and bumps it to the top).</summary>
    public void CopyToClipboard(ClipboardItem item)
    {
        SetClipboard(item, plainText: false);
        _store.Touch(item.Id);
    }

    /// <summary>Pastes arbitrary text (snippets / transforms) into the target window.</summary>
    public async Task PasteTextAsync(string text, IntPtr target)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }
        _monitor.SuppressFor(600);
        SetText(text);
        await Task.Delay(60).ConfigureAwait(true);
        NativeMethods.ForceForeground(target);
        await Task.Delay(90).ConfigureAwait(true);
        NativeMethods.SendCtrlV();
    }

    private void SetClipboard(ClipboardItem item, bool plainText)
    {
        _monitor.SuppressFor(600);

        try
        {
            if (item.Type == ClipItemType.Text)
            {
                if (string.IsNullOrEmpty(item.Text))
                {
                    return;
                }
                if (!plainText && item.HasRichText)
                {
                    SetRichText(item);
                }
                else
                {
                    SetText(item.Text);
                }
            }
            else if (plainText)
            {
                // Plain-text paste of a non-text item pastes its text representation (e.g. file paths).
                if (!string.IsNullOrEmpty(item.Text))
                {
                    SetText(item.Text);
                }
            }
            else if (item.Type == ClipItemType.Image && item.ImageData != null)
            {
                BitmapImage bmp = LoadBitmap(item.ImageData);
                Retry(() => WpfClipboard.SetImage(bmp));
            }
            else if (item.Type == ClipItemType.Files)
            {
                var paths = new StringCollection();
                paths.AddRange(item.FilePaths);
                Retry(() => WpfClipboard.SetFileDropList(paths));
            }
        }
        catch
        {
            // Best effort: a locked clipboard simply means this paste is skipped.
        }
    }

    private static void SetText(string text) =>
        Retry(() => WpfClipboard.SetText(text, WpfTextDataFormat.UnicodeText));

    /// <summary>Writes plain text plus HTML/RTF formats so the paste keeps its formatting.</summary>
    private static void SetRichText(ClipboardItem item) =>
        Retry(() =>
        {
            var data = new System.Windows.DataObject();
            data.SetText(item.Text!, WpfTextDataFormat.UnicodeText);
            if (!string.IsNullOrEmpty(item.Html))
            {
                data.SetText(item.Html, WpfTextDataFormat.Html);
            }
            if (!string.IsNullOrEmpty(item.Rtf))
            {
                data.SetText(item.Rtf, WpfTextDataFormat.Rtf);
            }
            WpfClipboard.SetDataObject(data, true);
        });

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

    private static void Retry(Action action)
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                action();
                return;
            }
            catch
            {
                Thread.Sleep(30);
            }
        }
    }
}
