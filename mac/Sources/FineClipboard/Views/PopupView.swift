import SwiftUI
import AppKit

struct PopupView: View {
    @ObservedObject var model: PopupModel
    @FocusState private var searchFocused: Bool

    private var tabs: [(tab: PopupTab, title: String)] {
        var t: [(PopupTab, String)] = [
            (.all, "全部"), (.text, "文本"), (.image, "图片"), (.files, "文件"),
            (.pinned, "置顶"), (.snippets, "片段"), (.passwords, "密码"),
        ]
        for l in model.lists { t.append((.list(l.id), l.name)) }
        return t
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

            Divider()

            // Tabs
            ScrollView(.horizontal, showsIndicators: false) {
                HStack(spacing: 6) {
                    ForEach(tabs, id: \.tab) { entry in
                        let isSel = entry.tab == model.tab
                        Text(entry.title)
                            .font(.system(size: 12))
                            .padding(.horizontal, 10).padding(.vertical, 4)
                            .background(isSel ? Color.accentColor : Color.secondary.opacity(0.12))
                            .foregroundColor(isSel ? .white : .primary)
                            .clipShape(Capsule())
                            .onTapGesture { model.select(tab: entry.tab) }
                    }
                }
                .padding(.horizontal, 10).padding(.vertical, 6)
            }

            Divider()

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
                    .onChange(of: model.selected) { sel in
                        withAnimation(.linear(duration: 0.08)) { proxy.scrollTo(sel, anchor: .center) }
                    }
                }
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Color(nsColor: .windowBackgroundColor))
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
                    .background(Color.secondary.opacity(0.18)).clipShape(Circle())
                    .foregroundColor(.secondary)
            }
        }
        .padding(.horizontal, 12).padding(.vertical, 7)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(isSel ? Color.accentColor.opacity(0.22) : Color.clear)
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
                .frame(width: 26, height: 26).clipShape(RoundedRectangle(cornerRadius: 4))
        } else {
            Image(systemName: row.symbol).foregroundColor(.secondary).frame(width: 22)
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
            Button("加入粘贴堆栈") { model.host?.addToStack(item: item) }
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
            Button(item.pinned ? "取消置顶" : "置顶") { model.host?.setPinned(item, !item.pinned) }
            Menu("移到列表") {
                Button("无(移出列表)") { model.host?.moveToList(item: item, listId: nil) }
                ForEach(model.lists) { l in
                    Button(l.name) { model.host?.moveToList(item: item, listId: l.id) }
                }
                Divider()
                Button("新建列表并加入…") { model.host?.newListAndAdd(item: item) }
            }
            Button("删除") { model.host?.delete(item: item) }
        } else if let s = row.snippet {
            Button("粘贴") { model.host?.pasteSnippet(s) }
        } else if let p = row.password {
            Button("粘贴(需主密码)") { model.host?.pastePassword(p) }
        }
    }

    private func looksLikeURL(_ s: String?) -> Bool {
        guard let s = s?.trimmingCharacters(in: .whitespacesAndNewlines) else { return false }
        return s.hasPrefix("http://") || s.hasPrefix("https://")
    }
}
