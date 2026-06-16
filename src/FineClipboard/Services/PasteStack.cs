using System.Collections.Generic;
using FineClipboard.Models;

namespace FineClipboard.Services;

/// <summary>
/// An ordered queue of items to paste one-by-one ("paste stack"). The user adds items from
/// the popup, then a global hotkey pastes the next item and advances. FIFO — items paste in
/// the order they were added, which matches how people collect-then-paste a sequence.
/// </summary>
public sealed class PasteStack
{
    private readonly Queue<ClipboardItem> _items = new();

    public int Count => _items.Count;

    public void Enqueue(ClipboardItem item) => _items.Enqueue(item);

    /// <summary>Removes and returns the next item, or null if the stack is empty.</summary>
    public ClipboardItem? Dequeue() => _items.Count > 0 ? _items.Dequeue() : null;

    public void Clear() => _items.Clear();
}
