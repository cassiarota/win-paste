import Foundation
import Combine

enum TextTransform: String, CaseIterable {
    case trim = "去除首尾空格"
    case collapse = "合并为单行"
    case upper = "转大写"
    case lower = "转小写"

    func apply(_ s: String) -> String {
        switch self {
        case .trim: return s.trimmingCharacters(in: .whitespacesAndNewlines)
        case .collapse:
            return s.split(whereSeparator: { $0 == "\n" || $0 == "\r" })
                .map { $0.trimmingCharacters(in: .whitespaces) }
                .joined(separator: " ")
        case .upper: return s.uppercased()
        case .lower: return s.lowercased()
        }
    }
}

/// A tab in the popup. "pinned" is kept as the storage field, but shown as favorites.
enum PopupTab: Hashable {
    case all, text, image, files, pinned, passwords

    var title: String {
        switch self {
        case .all: return "全部"
        case .text: return "文本"
        case .image: return "图片"
        case .files: return "文件"
        case .pinned: return "收藏"
        case .passwords: return "密码"
        }
    }
}

struct PopupRow: Identifiable {
    enum Payload { case item(ClipItem); case snippet(Snippet); case password(PasswordEntry) }
    let id: String
    let title: String
    let detail: String
    let symbol: String
    var badge: Int?
    let payload: Payload

    var item: ClipItem? { if case .item(let i) = payload { return i }; return nil }
    var snippet: Snippet? { if case .snippet(let s) = payload { return s }; return nil }
    var password: PasswordEntry? { if case .password(let p) = payload { return p }; return nil }
    var isPassword: Bool { if case .password = payload { return true }; return false }
}

/// Bridge to the AppKit side (paste, dialogs, window control).
protocol PopupHost: AnyObject {
    func paste(item: ClipItem, plain: Bool)
    func copy(item: ClipItem)
    func pastePassword(_ entry: PasswordEntry)
    func setPinned(_ item: ClipItem, _ pinned: Bool)
    func delete(item: ClipItem)
    func clearHistoryKeepingFavorites()
    func editAndPaste(item: ClipItem)
    func transformAndPaste(item: ClipItem, transform: TextTransform)
    func openLink(item: ClipItem)
    func revealFile(item: ClipItem)
    func saveImage(item: ClipItem)
    func closePopup()
}

/// Drives the popup's content and selection. The view observes it; the AppKit
/// key-monitor and host mutate it.
final class PopupModel: ObservableObject {
    @Published var rows: [PopupRow] = []
    @Published var selected = 0
    @Published var search = "" { didSet { applyFilter() } }
    @Published var tab: PopupTab = .all

    let store: Store
    let vault: Vault
    weak var host: PopupHost?

    init(store: Store, vault: Vault) {
        self.store = store
        self.vault = vault
    }

    func reload() {
        applyFilter()
    }

    func select(tab newTab: PopupTab) {
        tab = newTab
        search = ""        // triggers applyFilter via didSet
        applyFilter()
    }

    private func cap() -> Int { Int(store.setting(Store.maxItemsKey) ?? "1000") ?? 1000 }

    func applyFilter() {
        let term = search.trimmingCharacters(in: .whitespaces)
        var built: [PopupRow] = []

        switch tab {
        case .passwords:
            built = vault.entries()
                .filter { term.isEmpty || $0.name.localizedCaseInsensitiveContains(term) }
                .map { PopupRow(id: "pw-\($0.id)", title: $0.name, detail: "••••••", symbol: "key.fill", payload: .password($0)) }
        default:
            let items: [ClipItem]
            if !term.isEmpty {
                items = scopeItems(store.searchItems(term, limit: cap()))
            } else {
                items = baseItems()
            }
            built = items.map { rowForItem($0) }
        }

        // Quick-paste badges 1-9 only when not searching.
        if term.isEmpty {
            for i in 0..<min(9, built.count) { built[i].badge = i + 1 }
        }
        rows = built
        if selected >= rows.count { selected = max(0, rows.count - 1) }
        if selected < 0 { selected = 0 }
    }

    private func baseItems() -> [ClipItem] {
        switch tab {
        case .all: return store.allItems(limit: cap())
        case .text: return store.itemsByKind(.text, limit: cap())
        case .image: return store.itemsByKind(.image, limit: cap())
        case .files: return store.itemsByKind(.files, limit: cap())
        case .pinned: return store.pinnedItems()
        default: return []
        }
    }

    /// Restrict a search result set to the current item-based tab.
    private func scopeItems(_ items: [ClipItem]) -> [ClipItem] {
        switch tab {
        case .text: return items.filter { $0.kind == .text }
        case .image: return items.filter { $0.kind == .image }
        case .files: return items.filter { $0.kind == .files }
        case .pinned: return items.filter { $0.pinned }
        default: return items
        }
    }

    private func rowForItem(_ i: ClipItem) -> PopupRow {
        let symbol: String
        switch i.kind {
        case .text: symbol = "doc.plaintext"
        case .image: symbol = "photo"
        case .files: symbol = "folder"
        }
        let detail = i.pinned ? "★ " + (i.source ?? "") : (i.source ?? "")
        return PopupRow(id: "item-\(i.id)", title: i.preview, detail: detail, symbol: symbol, payload: .item(i))
    }

    // MARK: - actions invoked by the view / key monitor

    func move(_ delta: Int) {
        guard !rows.isEmpty else { return }
        selected = min(max(0, selected + delta), rows.count - 1)
    }

    func activateSelected(plain: Bool) {
        guard rows.indices.contains(selected) else { return }
        activate(rows[selected], plain: plain)
    }

    func activate(_ row: PopupRow, plain: Bool) {
        switch row.payload {
        case .item(let i): host?.paste(item: i, plain: plain)
        case .snippet: break
        case .password(let p): host?.pastePassword(p)
        }
    }

    func activateBadge(_ n: Int) {
        guard let idx = rows.firstIndex(where: { $0.badge == n }) else { return }
        selected = idx
        activate(rows[idx], plain: false)
    }
}
