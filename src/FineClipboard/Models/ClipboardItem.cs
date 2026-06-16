using System;

namespace FineClipboard.Models;

/// <summary>A single clipboard history entry.</summary>
public sealed class ClipboardItem
{
    public long Id { get; set; }

    public ClipItemType Type { get; set; }

    /// <summary>For Text: the full text. For Files: newline-joined absolute paths. Null for Image.</summary>
    public string? Text { get; set; }

    /// <summary>PNG-encoded bytes for Image entries; null otherwise.</summary>
    public byte[]? ImageData { get; set; }

    /// <summary>Optional HTML payload captured alongside Text (format fidelity); null if none.</summary>
    public string? Html { get; set; }

    /// <summary>Optional RTF payload captured alongside Text (format fidelity); null if none.</summary>
    public string? Rtf { get; set; }

    /// <summary>Recognized text inside an Image (OCR), filled asynchronously; null otherwise.</summary>
    public string? OcrText { get; set; }

    /// <summary>True when this text entry carries HTML/RTF formatting that can be pasted.</summary>
    public bool HasRichText => !string.IsNullOrEmpty(Html) || !string.IsNullOrEmpty(Rtf);

    /// <summary>Short text shown in the popup list.</summary>
    public string Preview { get; set; } = string.Empty;

    /// <summary>Process name of the app that owned the foreground when copied (best effort).</summary>
    public string? SourceApp { get; set; }

    public bool Pinned { get; set; }

    /// <summary>UTC creation/last-used time.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Stable identifier used for cross-device sync; null until first synced.</summary>
    public string? SyncUuid { get; set; }

    /// <summary>Logical clock (Unix ms) for last-write-wins sync.</summary>
    public long UpdatedMs { get; set; }

    public string[] FilePaths =>
        Type == ClipItemType.Files && !string.IsNullOrEmpty(Text)
            ? Text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            : Array.Empty<string>();
}
