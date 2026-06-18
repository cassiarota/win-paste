import SwiftUI
import AppKit

final class SettingsModel: ObservableObject {
    let host: AppControl
    @Published var popupLabel = ""
    @Published var plainLabel = ""
    @Published var shotLabel = ""
    @Published var recording: String?   // "popup" / "plain" / "shot" while capturing
    private var monitor: Any?

    init(host: AppControl) { self.host = host; loadLabels() }

    func loadLabels() {
        popupLabel = HotkeyCombo.parse(host.store.setting(Store.hotkeyPopupKey), default: .defaultPopup).display()
        plainLabel = HotkeyCombo.parse(host.store.setting(Store.hotkeyPlainKey), default: .defaultPlain).display()
        shotLabel = HotkeyCombo.parse(host.store.setting(Store.hotkeyShotKey), default: .defaultShot).display()
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
        let target = recording ?? "popup"
        let ok = host.trySetHotkey(target: target, combo: combo)
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
        switch target {
        case "popup": return popupLabel
        case "plain": return plainLabel
        default: return shotLabel
        }
    }
}

struct SettingsView: View {
    let host: AppControl
    let onSetPassword: () -> Void
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
    @State private var selectedTab = 0
    @State private var passwordConfigured = false

    private var store: Store { host.store }

    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            header
            TabView(selection: $selectedTab) {
                tabPage {
                    Toggle("开机自启动", isOn: $startup)
                        .onChange(of: startup) { LoginItem.setEnabled($0) }
                    Toggle("复制时播放提示音", isOn: $sound)
                        .onChange(of: sound) { store.setSetting(Store.soundEnabledKey, $0 ? "1" : "0") }
                }
                .tabItem { Label("通用", systemImage: "gearshape") }
                .tag(0)

                tabPage {
                    Toggle("暂停记录(隐私模式)", isOn: $paused)
                        .onChange(of: paused) { host.setRecordingPaused($0) }
                    HStack {
                        Text("最多保留(非收藏记录)").frame(width: 190, alignment: .leading)
                        Picker("", selection: $maxItems) {
                            Text("200 条").tag("200"); Text("500 条").tag("500")
                            Text("1000 条").tag("1000"); Text("5000 条").tag("5000")
                        }
                        .labelsHidden()
                        .frame(width: 150)
                        .onChange(of: maxItems) { store.setSetting(Store.maxItemsKey, $0) }
                    }
                    HStack {
                        Text("历史过期时间").frame(width: 190, alignment: .leading)
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
                        .frame(height: 108)
                        .scrollContentBackground(.hidden)
                        .background(.white.opacity(0.14))
                        .onChange(of: exclusions) { store.setSetting(Store.exclusionsKey, $0) }
                    Text("已保存 \(count) 条历史记录")
                        .font(.caption)
                        .foregroundColor(.secondary)
                }
                .tabItem { Label("历史与隐私", systemImage: "clock.arrow.circlepath") }
                .tag(1)

                tabPage {
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
                .tabItem { Label("外观", systemImage: "paintbrush") }
                .tag(2)

                tabPage {
                    Text("点击按钮后按下新的组合键(需含修饰键),Esc 取消。")
                        .font(.caption)
                        .foregroundColor(.secondary)
                    Grid(alignment: .leading, horizontalSpacing: 12, verticalSpacing: 8) {
                        GridRow {
                            Text("打开剪贴板历史").frame(width: 200, alignment: .leading)
                            Button(model.label(for: "popup")) { model.startRecording("popup") }.frame(width: 170)
                        }
                        GridRow {
                            Text("纯文本粘贴最近一条").frame(width: 200, alignment: .leading)
                            Button(model.label(for: "plain")) { model.startRecording("plain") }.frame(width: 170)
                        }
                        GridRow {
                            Text("截图").frame(width: 200, alignment: .leading)
                            Button(model.label(for: "shot")) { model.startRecording("shot") }.frame(width: 170)
                        }
                    }
                }
                .tabItem { Label("快捷键", systemImage: "keyboard") }
                .tag(3)

                tabPage {
                    Text("云同步使用同步口令端到端加密；密码数据不会参与同步。")
                        .font(.caption)
                        .foregroundColor(.secondary)
                    Button("打开云同步设置…") { host.showSyncSettings() }
                }
                .tabItem { Label("同步", systemImage: "arrow.triangle.2.circlepath") }
                .tag(4)

                tabPage {
                    Text("设置主密码后即可使用加密密码库。")
                        .font(.caption)
                        .foregroundColor(.secondary)
                    if passwordConfigured {
                        Text("密码已设置")
                    } else {
                        Button("设置密码") {
                            onSetPassword()
                            passwordConfigured = host.vault.isConfigured
                        }
                    }
                }
                .tabItem { Label("密码", systemImage: "lock") }
                .tag(5)
            }
            .frame(height: 430)
        }
        .padding(20)
        .background(.regularMaterial)
        .frame(width: 560)
        .onAppear(perform: load)
        .onDisappear { model.cancel() }
    }

    private var header: some View {
        HStack(spacing: 12) {
            Image(systemName: "doc.on.clipboard")
                .symbolRenderingMode(.hierarchical)
                .font(.system(size: 34, weight: .semibold))
                .foregroundStyle(.blue)
                .frame(width: 44, height: 44)
                .background(.thinMaterial)
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
        passwordConfigured = host.vault.isConfigured
        model.loadLabels()
    }

    private func tabPage<C: View>(@ViewBuilder _ content: () -> C) -> some View {
        VStack(alignment: .leading, spacing: 10) {
            content()
        }
        .padding(18)
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    private func labeled<C: View>(_ title: String, @ViewBuilder _ content: () -> C) -> some View {
        VStack(alignment: .leading, spacing: 4) {
            Text(title).font(.callout)
            content()
        }
    }
}
