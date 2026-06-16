import Foundation

/// An ordered queue of items to paste one-by-one ("paste stack"). The user adds items from
/// the popup, then a global hotkey pastes the next item and advances. FIFO — items paste in
/// the order they were added.
final class PasteStack {
    private var items: [ClipItem] = []

    var count: Int { items.count }

    func enqueue(_ item: ClipItem) { items.append(item) }

    /// Removes and returns the next item, or nil if empty.
    func dequeue() -> ClipItem? { items.isEmpty ? nil : items.removeFirst() }

    func clear() { items.removeAll() }
}
