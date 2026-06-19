import SwiftUI
import AppKit

struct PopupView: View {
    @ObservedObject var model: PopupModel
    @FocusState private var searchFocused: Bool
    @State private var pageScrollPending = false

    private var tabs: [(tab: PopupTab, title: String)] {
        var values: [(PopupTab, String)] = [
            (.all, "全部"), (.text, "文本"), (.image, "图片"), (.files, "文件"),
            (.pinned, "收藏"), (.passwords, "密码"),
        ]
        values.append(contentsOf: model.lists.map { (.list($0.id), $0.name) })
        return values
    }

    var body: some View {
        VStack(spacing: 0) {
            // Search
            HStack(spacing: 6) {
                Image(systemName: "magnifyingglass").foregroundColor(.secondary)
                TextField("搜索…", text: $model.search)
                    .textFieldStyle(.plain)
                    .focused($searchFocused)
            }
            .padding(.horizontal, 12).padding(.vertical, 8)
            .background(.white.opacity(0.12))
            .padding(.horizontal, 10)
            .padding(.top, 10)

            // Tabs
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: 6) {
                    ForEach(tabs, id: \.tab) { entry in
                        let isSel = entry.tab == model.tab
                        Text(entry.title)
                            .font(.system(size: 12))
                            .padding(.horizontal, 10).padding(.vertical, 4)
                            .background(isSel ? Color.accentColor.opacity(0.88) : Color.secondary.opacity(0.12))
                            .foregroundColor(isSel ? .white : .primary)
                            .onTapGesture { model.select(tab: entry.tab) }
                            .contextMenu {
                                Button("新建列表…") { model.host?.createList() }
                                if case .list(let id) = entry.tab,
                                   let list = model.lists.first(where: { $0.id == id }) {
                                    Button("删除当前列表…") { model.host?.deleteList(list) }
                                }
                            }
                    }
                }
                .padding(.horizontal, 10).padding(.vertical, 6)
            }

            // Rows
            if model.rows.isEmpty {
                emptyState
            } else {
                ScrollViewReader { proxy in
                    ScrollView {
                        LazyVStack(spacing: 0) {
                            ForEach(Array(model.rows.enumerated()), id: \.element.id) { idx, row in
                                rowView(row, index: idx)
                                    .id(idx)
                            }
                        }
                    }
                    .background(PageWheelCapture { direction, visibleRows in
                        let previous = model.selected
                        pageScrollPending = true
                        model.move(direction * visibleRows)
                        if model.selected == previous { pageScrollPending = false }
                    })
                    .onChange(of: model.selected) { sel in
                        if pageScrollPending {
                            pageScrollPending = false
                            proxy.scrollTo(sel, anchor: .top)
                        } else {
                            withAnimation(.linear(duration: 0.08)) { proxy.scrollTo(sel, anchor: .center) }
                        }
                    }
                }
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(.regularMaterial)
        .onAppear { searchFocused = true }
    }

    private var emptyState: some View {
        VStack(spacing: 8) {
            Image(systemName: "tray").font(.system(size: 28)).foregroundColor(.secondary)
            Text(model.search.isEmpty ? "暂无内容" : "没有匹配的结果")
                .font(.system(size: 13)).foregroundColor(.secondary)
            if model.search.isEmpty && model.tab == .all {
                Text("复制一些文字、图片或文件,它们会出现在这里。")
                    .font(.system(size: 11)).foregroundColor(.secondary)
                    .multilineTextAlignment(.center).padding(.horizontal, 24)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }

    private func rowView(_ row: PopupRow, index: Int) -> some View {
        let isSel = index == model.selected
        return HStack(spacing: 8) {
            thumbnail(row)
            VStack(alignment: .leading, spacing: 1) {
                Text(row.title.isEmpty ? " " : row.title)
                    .font(.system(size: 13)).lineLimit(1)
                if !row.detail.isEmpty {
                    Text(row.detail).font(.system(size: 10)).foregroundColor(.secondary).lineLimit(1)
                }
            }
            Spacer(minLength: 4)
            if let b = row.badge {
                Text("\(b)").font(.system(size: 10, weight: .semibold))
                    .frame(width: 16, height: 16)
                    .background(Color.secondary.opacity(0.18))
                    .foregroundColor(.secondary)
            }
        }
        .padding(.horizontal, 12).padding(.vertical, 7)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(isSel ? Color.accentColor.opacity(0.20) : Color.white.opacity(0.08))
        .overlay(Rectangle().stroke(isSel ? Color.accentColor.opacity(0.28) : Color.white.opacity(0.10)))
        .padding(.horizontal, 8)
        .padding(.vertical, 3)
        .contentShape(Rectangle())
        .onTapGesture(count: 2) { model.activate(row, plain: false) }
        .onTapGesture(count: 1) { model.selected = index }
        .onDrag { dragProvider(row) }
        .contextMenu { menu(for: row) }
    }

    @ViewBuilder
    private func thumbnail(_ row: PopupRow) -> some View {
        if let data = row.item?.data, row.item?.kind == .image, let img = NSImage(data: data) {
            Image(nsImage: img).resizable().aspectRatio(contentMode: .fill)
                .frame(width: 30, height: 30).clipped()
        } else {
            Image(systemName: row.symbol)
                .foregroundColor(.secondary)
                .frame(width: 30, height: 30)
                .background(Color.secondary.opacity(0.10))
        }
    }

    private func dragProvider(_ row: PopupRow) -> NSItemProvider {
        if let item = row.item {
            switch item.kind {
            case .text, .files:
                if let t = Paste.dragText(item) { return NSItemProvider(object: t as NSString) }
            case .image:
                if let data = item.data, let img = NSImage(data: data) { return NSItemProvider(object: img) }
            }
        }
        return NSItemProvider()
    }

    @ViewBuilder
    private func menu(for row: PopupRow) -> some View {
        if let item = row.item {
            Button("粘贴") { model.activate(row, plain: false) }
            Button("纯文本粘贴") { model.host?.paste(item: item, plain: true) }
            if item.kind == .text {
                Button("编辑后粘贴…") { model.host?.editAndPaste(item: item) }
                Menu("转换后粘贴") {
                    ForEach(TextTransform.allCases, id: \.self) { tr in
                        Button(tr.rawValue) { model.host?.transformAndPaste(item: item, transform: tr) }
                    }
                }
            }
            Button("复制(放回剪贴板)") { model.host?.copy(item: item) }
            Divider()
            if item.kind == .text, looksLikeURL(item.text) {
                Button("打开链接") { model.host?.openLink(item: item) }
            }
            if item.kind == .files {
                Button("在访达中显示") { model.host?.revealFile(item: item) }
            }
            if item.kind == .image {
                Button("图片另存为…") { model.host?.saveImage(item: item) }
            }
            Divider()
            Button(item.pinned ? "取消收藏" : "收藏") { model.host?.setPinned(item, !item.pinned) }
            Menu("移到列表") {
                Button("移出列表") { model.host?.moveToList(item: item, listId: nil) }
                ForEach(model.lists) { list in
                    Button(list.name) { model.host?.moveToList(item: item, listId: list.id) }
                }
                Divider()
                Button("新建列表并加入…") { model.host?.newListAndAdd(item: item) }
            }
            Button("删除") { model.host?.delete(item: item) }
        } else if let p = row.password {
            Button("粘贴(需主密码)") { model.host?.pastePassword(p) }
        }
    }

    private func looksLikeURL(_ s: String?) -> Bool {
        guard let s = s?.trimmingCharacters(in: .whitespacesAndNewlines) else { return false }
        return s.hasPrefix("http://") || s.hasPrefix("https://")
    }
}

/// Consumes the system line-count/speed setting and turns each physical wheel event into one
/// contiguous viewport. Precision trackpads advance once per gesture rather than once per frame.
private struct PageWheelCapture: NSViewRepresentable {
    let onPage: (Int, Int) -> Void

    func makeCoordinator() -> Coordinator { Coordinator(onPage: onPage) }

    func makeNSView(context: Context) -> NSView {
        let view = NSView(frame: .zero)
        context.coordinator.view = view
        context.coordinator.install()
        return view
    }

    func updateNSView(_ nsView: NSView, context: Context) {
        context.coordinator.onPage = onPage
    }

    final class Coordinator {
        weak var view: NSView?
        var onPage: (Int, Int) -> Void
        private var monitor: Any?

        init(onPage: @escaping (Int, Int) -> Void) { self.onPage = onPage }
        deinit { if let monitor { NSEvent.removeMonitor(monitor) } }

        func install() {
            monitor = NSEvent.addLocalMonitorForEvents(matching: .scrollWheel) { [weak self] event in
                guard let self, let view = self.view, event.window === view.window else { return event }
                let local = view.convert(event.locationInWindow, from: nil)
                guard view.bounds.contains(local) else { return event }

                if event.hasPreciseScrollingDeltas && event.phase != .began && !event.phase.isEmpty {
                    return nil
                }
                let delta = event.scrollingDeltaY
                guard abs(delta) > 0.01 else { return nil }
                let direction = delta < 0 ? 1 : -1
                let visibleRows = max(1, Int(view.bounds.height / 52))
                self.onPage(direction, visibleRows)
                return nil
            }
        }
    }
}
