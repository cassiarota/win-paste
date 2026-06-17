import SwiftUI
import AppKit

final class SettingsModel: ObservableObject {
    let host: AppControl
    @Published var popupLabel = ""
    @Published var plainLabel = ""
    @Published var recording: String?   // "popup" / "plain" while capturing
    private var monitor: Any?

    init(host: AppControl) { self.host = host; loadLabels() }

    func loadLabels() {
        popupLabel = HotkeyCombo.parse(host.store.setting(Store.hotkeyPopupKey), default: .defaultPopup).display()
        plainLabel = HotkeyCombo.parse(host.store.setting(Store.hotkeyPlainKey), default: .defaultPlain).display()
    }

    func startRecording(_ target: String) {
        stopMonitor()
        recording = target
        host.suspendHotkeys()
        monitor = NSEvent.addLocalMonitorForEvents(matching: .keyDown) { [weak self] event in
            self?.captured(event); return nil
        }
    }

    private func captured(_ event: NSEvent) {
        if event.keyCode == 53 { cancel(); return }                  // Esc
        guard let combo = HotkeyCombo.from(event: event) else { return } // need a modifier; keep waiting
        let isPopup = recording == "popup"
        let ok = host.trySetHotkey(popup: isPopup, combo: combo)
        stopMonitor()
        recording = nil
        loadLabels()
        if !ok { Prompt.info("无法注册该快捷键", "可能已被其它程序占用,或与另一个快捷键冲突,请换一个。") }
    }

    func cancel() {
        stopMonitor()
        recording = nil
        host.resumeHotkeys()
        loadLabels()
    }

    private func stopMonitor() {
        if let monitor { NSEvent.removeMonitor(monitor) }
        monitor = nil
    }

    func label(for target: String) -> String {
        if recording == target { return "按下快捷键…" }
        return target == "popup" ? popupLabel : plainLabel
    }
}

struct SettingsView: View {
    let host: AppControl
    let onManageSnippets: () -> Void
    let onManageLists: () -> Void
    let onManagePasswords: () -> Void
    let onMasterPassword: () -> Void
    let masterTitle: () -> String
    @StateObject var model: SettingsModel

    @State private var startup = false
    @State private var sound = false
    @State private var paused = false
    @State private var maxItems = "1000"
    @State private var expiry = "0"
    @State private var theme = "system"
    @State private var popupSize = "medium"
    @State private var exclusions = ""
    @State private var count = 0

    private var store: Store { host.store }

    var body: some View {
        ScrollView {
            VStack(alignment: .leading, spacing: 14) {
                header

                section("通用") {
                    Toggle("开机自启动", isOn: $startup)
                        .onChange(of: startup) { LoginItem.setEnabled($0) }
                    Toggle("复制时播放提示音", isOn: $sound)
                        .onChange(of: sound) { store.setSetting(Store.soundEnabledKey, $0 ? "1" : "0") }
                }

                section("历史与隐私") {
                    Toggle("暂停记录(隐私模式)", isOn: $paused)
                        .onChange(of: paused) { host.setRecordingPaused($0) }
                    labeled("最多保留(非置顶记录)") {
                        Picker("", selection: $maxItems) {
                            Text("200 条").tag("200"); Text("500 条").tag("500")
                            Text("1000 条").tag("1000"); Text("5000 条").tag("5000")
                        }
                        .labelsHidden()
                        .frame(width: 150)
                        .onChange(of: maxItems) { store.setSetting(Store.maxItemsKey, $0) }
                    }
                    labeled("历史过期时间") {
                        Picker("", selection: $expiry) {
                            Text("永不过期").tag("0"); Text("1 天").tag("1"); Text("7 天").tag("7")
                            Text("30 天").tag("30"); Text("90 天").tag("90")
                        }
                        .labelsHidden()
                        .frame(width: 150)
                        .onChange(of: expiry) {
                            store.setSetting(Store.expiryDaysKey, $0)
                            store.purgeExpired(days: Int($0) ?? 0); count = store.count()
                        }
                    }
                    Text("排除规则")
                        .font(.callout.weight(.medium))
                    Text("每行一个应用名(如 1Password、KeePassXC),来自这些程序的复制不会被记录。")
                        .font(.caption)
                        .foregroundColor(.secondary)
                    TextEditor(text: $exclusions)
                        .font(.system(size: 12))
                        .frame(height: 72)
                        .scrollContentBackground(.hidden)
                        .background(.white.opacity(0.14), in: RoundedRectangle(cornerRadius: 10))
                        .onChange(of: exclusions) { store.setSetting(Store.exclusionsKey, $0) }
                }

                section("外观") {
                    HStack(spacing: 18) {
                        labeled("主题") {
                            Picker("", selection: $theme) {
                                Text("跟随系统").tag("system"); Text("浅色").tag("light"); Text("深色").tag("dark")
                            }
                            .labelsHidden()
                            .frame(width: 120)
                            .onChange(of: theme) { store.setSetting(Store.themeKey, $0); host.applyAppearance($0) }
                        }
                        labeled("弹窗大小") {
                            Picker("", selection: $popupSize) {
                                Text("小").tag("small"); Text("中").tag("medium"); Text("大").tag("large")
                            }
                            .labelsHidden()
                            .frame(width: 96)
                            .onChange(of: popupSize) { store.setSetting(Store.popupSizeKey, $0) }
                        }
                    }
                }

                section("快捷键") {
                    Text("点击按钮后按下新的组合键(需含修饰键),Esc 取消。")
                        .font(.caption)
                        .foregroundColor(.secondary)
                    Grid(alignment: .leading, horizontalSpacing: 12, verticalSpacing: 8) {
                        GridRow {
                            Text("打开剪贴板历史")
                            Button(model.label(for: "popup")) { model.startRecording("popup") }.frame(width: 170)
                        }
                        GridRow {
                            Text("纯文本粘贴最近一条")
                            Button(model.label(for: "plain")) { model.startRecording("plain") }.frame(width: 170)
                        }
                        GridRow {
                            Text("截图")
                            Text(host.screenshotHotkeyDisplay())
                                .frame(width: 170)
                                .padding(.vertical, 6)
                                .background(.white.opacity(0.14), in: RoundedRectangle(cornerRadius: 9))
                        }
                    }
                }

                section("同步") {
                    Text("云同步使用同步口令端到端加密；密码数据不会参与同步。")
                        .font(.caption)
                        .foregroundColor(.secondary)
                    Button("打开云同步设置…") { host.showSyncSettings() }
                }

                section("密码") {
                    Text("主密码用于加密本机密码库；锁定后查看 / 粘贴密码需重新输入。")
                        .font(.caption)
                        .foregroundColor(.secondary)
                    HStack {
                        Button(masterTitle(), action: onMasterPassword)
                        Button("管理密码…", action: onManagePasswords)
                        Button("锁定密码") { host.lockVault() }
                    }
                }

                section("维护") {
                    Text("已保存 \(count) 条历史记录")
                        .foregroundColor(.secondary)
                    HStack {
                        Button("管理常用片段…", action: onManageSnippets)
                        Button("管理列表…", action: onManageLists)
                    }
                    HStack {
                        Button("清空历史(保留置顶)") {
                            if Prompt.confirm("确定清空历史吗?", "置顶项会保留。") {
                                store.clear(keepPinned: true); count = store.count()
                            }
                        }
                        Button("全部清空") {
                            if Prompt.confirm("确定清空全部历史吗?", "包括置顶项,不可恢复。") {
                                store.clear(keepPinned: false); count = store.count()
                            }
                        }
                    }
                }

                Text("提示:首次使用需在「系统设置 → 隐私与安全性 → 辅助功能」中允许 FineClipboard,才能自动粘贴。")
                    .font(.caption)
                    .foregroundColor(.secondary)
            }
            .padding(20)
        }
        .background(.regularMaterial)
        .frame(width: 480)
        .onAppear(perform: load)
    }

    private var header: some View {
        HStack(spacing: 12) {
            Image(systemName: "doc.on.clipboard")
                .symbolRenderingMode(.hierarchical)
                .font(.system(size: 34, weight: .semibold))
                .foregroundStyle(.blue)
                .frame(width: 44, height: 44)
                .background(.thinMaterial, in: RoundedRectangle(cornerRadius: 12))
            VStack(alignment: .leading, spacing: 2) {
                Text("FineClipboard").font(.title2.weight(.semibold))
                Text("版本 \(AppInfo.version)").font(.caption).foregroundColor(.secondary)
            }
        }
        .padding(.bottom, 4)
    }

    private func load() {
        startup = LoginItem.isEnabled
        sound = store.setting(Store.soundEnabledKey) == "1"
        paused = host.isRecordingPaused
        maxItems = store.setting(Store.maxItemsKey) ?? "1000"
        expiry = store.setting(Store.expiryDaysKey) ?? "0"
        theme = store.setting(Store.themeKey) ?? "system"
        popupSize = store.setting(Store.popupSizeKey) ?? "medium"
        exclusions = store.setting(Store.exclusionsKey) ?? ""
        count = store.count()
        model.loadLabels()
    }

    private func section<C: View>(_ title: String, @ViewBuilder _ content: () -> C) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            Text(title).font(.headline)
            content()
        }
        .padding(14)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.thinMaterial, in: RoundedRectangle(cornerRadius: 16))
        .overlay(RoundedRectangle(cornerRadius: 16).stroke(.white.opacity(0.18)))
    }

    private func labeled<C: View>(_ title: String, @ViewBuilder _ content: () -> C) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(title).font(.callout)
            content()
        }
    }
}
