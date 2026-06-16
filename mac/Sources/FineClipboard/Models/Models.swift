import Foundation

/// The three content kinds we capture, mirroring the Windows app.
enum ClipKind: Int {
    case text = 0
    case image = 1
    case files = 2
}

/// One clipboard history entry.
struct ClipItem: Identifiable, Equatable {
    var id: Int64
    var kind: ClipKind
    /// Text content (text kind) or newline-joined paths (files kind); nil for images.
    var text: String?
    /// PNG bytes for image kind; nil otherwise.
    var data: Data?
    /// Short display preview.
    var preview: String
    var pinned: Bool
    var listId: Int64?
    /// Frontmost app name when captured (for exclusion rules).
    var source: String?
    var createdAt: Double
    var lastUsed: Double
    /// Optional HTML payload captured alongside text (format fidelity).
    var html: String? = nil
    /// Optional RTF payload captured alongside text (format fidelity).
    var rtf: String? = nil
    /// Recognized text inside an image (OCR), filled asynchronously.
    var ocrText: String? = nil
    /// Stable identifier used for cross-device sync; nil until first synced.
    var syncUuid: String? = nil
    /// Logical clock (Unix ms) for last-write-wins sync.
    var updatedMs: Int64 = 0

    /// True when this text entry carries HTML/RTF formatting that can be pasted.
    var hasRichText: Bool { !(html?.isEmpty ?? true) || !(rtf?.isEmpty ?? true) }

    static func == (a: ClipItem, b: ClipItem) -> Bool { a.id == b.id }
}

struct Snippet: Identifiable, Equatable {
    var id: Int64
    var name: String
    var content: String
}

struct PasswordEntry: Identifiable, Equatable {
    var id: Int64
    var name: String
}

struct ClipList: Identifiable, Equatable {
    var id: Int64
    var name: String
}
