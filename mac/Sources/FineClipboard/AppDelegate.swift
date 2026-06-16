import Cocoa
import SwiftUI

final class AppDelegate: NSObject, NSApplicationDelegate, PopupHost, AppControl {
    let store = Store()
    lazy var vault = Vault(store)

    private let monitor = ClipboardMonitor()
    private let pasteStack = PasteStack()
    private lazy var sync = SyncEngine(store)
    private var syncTimer: Timer?
    private var syncing = false
    private var syncWC: NSWindowController?
    private lazy var popup = PopupController(model: PopupModel(store: store, vault: vault))

    private var statusItem: NSStatusItem!
    private var openItem: NSMenuItem!
    private var pauseItem: NSMenuItem!
    private var startupItem: NSMenuItem!
    private var updateItem: NSMenuItem!
    private var stackItem: NSMenuItem!
    private var updateURL: String?

    private var previousApp: NSRunningApplication?
    private var purgeTimer: Timer?

    private var settingsWC: NSWindowController?
    private var snippetsWC: NSWindowController?
    private var listsWC: NSWindowController?
    private var passwordsWC: NSWindowController?

    private static let popupHotkeyID: UInt32 = 1
    private static let plainHotkeyID: UInt32 = 2
    private static let stackHotkeyID: UInt32 = 3
    private static let shotHotkeyID: UInt32 = 4

    // MARK: - lifecycle

    func applicationDidFinishLaunching(_ notification: Notification) {
        NSApp.setActivationPolicy(.accessory)
        Appearance.apply(store.setting(Store.themeKey))

        purgeExpiredNow()
        purgeTimer = Timer.scheduledTimer(withTimeInterval: 3600, repeats: true) { [weak self] _ in self?.purgeExpiredNow() }
        syncTimer = Timer.scheduledTimer(withTimeInterval: 45, repeats: true) { [weak self] _ in self?.runSync() }

        monitor.shouldSkipSource = { [weak self] in self?.isExcluded($0) ?? false }
        monitor.onCapture = { [weak self] cap in self?.handleCapture(cap) }
        monitor.start()

        popup.model.host = self
        popup.sizeTag = { [weak self] in self?.store.setting(Store.popupSizeKey) ?? "medium" }

        reloadHotkeys()
        setupStatusItem()
        showFirstRun()
        Updater.check { [weak self] result in self?.applyUpdate(result, silent: true) }
    }

    func applicationWillTerminate(_ notification: Notification) {
        purgeTimer?.invalidate()
        monitor.stop()
        HotkeyManager.shared.unregisterAll()
    }

    // MARK: - capture

    private func handleCapture(_ cap: Capture) {
        let id = store.add(kind: cap.kind, text: cap.text, data: cap.data, preview: cap.preview,
                           source: cap.source, html: cap.html, rtf: cap.rtf)
        store.trimOverflow(max: Int(store.setting(Store.maxItemsKey) ?? "1000") ?? 1000)

        // Run OCR on captured images in the background, then store the recognized text so the
        // screenshot becomes searchable. On-device (Vision), never blocks capture.
        if cap.kind == .image, let png = cap.data {
            OCR.recognize(png) { [weak self] text in
                guard let text else { return }
                DispatchQueue.main.async {
                    self?.store.updateOcrText(id, text)
                    self?.popup.reloadIfVisible()
                }
            }
        }

        if store.setting(Store.soundEnabledKey) == "1" { NSSound(named: "Pop")?.play() }
        popup.reloadIfVisible()
    }

    private func isExcluded(_ source: String?) -> Bool {
        guard let source, let raw = store.setting(Store.exclusionsKey), !raw.isEmpty else { return false }
        for line in raw.split(whereSeparator: { $0 == "\n" || $0 == "\r" }) {
            var rule = line.trimmingCharacters(in: .whitespaces)
            if rule.lowercased().hasSuffix(".app") { rule = String(rule.dropLast(4)) }
            if !rule.isEmpty && rule.compare(source, options: .caseInsensitive) == .orderedSame { return true }
        }
        return false
    }

    private func purgeExpiredNow() {
        store.purgeExpired(days: Int(store.setting(Store.expiryDaysKey) ?? "0") ?? 0)
    }

    /// Background sync round if enabled; refreshes the popup if it pulled changes.
    private func runSync() {
        guard !syncing, sync.ready else { return }
        syncing = true
        Task { @MainActor in
            defer { self.syncing = false }
            do {
                _ = try await self.sync.syncNow()
                self.popup.reloadIfVisible()
            } catch {
                // Network hiccups are non-fatal; the next tick retries.
            }
        }
    }

    private func showSync() {
        if syncWC == nil {
            syncWC = WindowFactory.make(title: "云同步", width: 430, height: 560, root: SyncView(engine: sync))
        }
        activate(syncWC)
    }

    @objc private func openSyncAction() { showSync() }

    // MARK: - hotkeys / popup

    private func showPopup() {
        previousApp = NSWorkspace.shared.frontmostApplication
        popup.show()
    }

    private func pasteRecentPlain() {
        previousApp = NSWorkspace.shared.frontmostApplication
        if let item = store.mostRecentText(), let text = item.text {
            pasteText(text)
            store.touch(item.id)
        }
    }

    @discardableResult
    private func reloadHotkeysInternal() -> (Bool, Bool) {
        HotkeyManager.shared.unregisterAll()
        let popupCombo = HotkeyCombo.parse(store.setting(Store.hotkeyPopupKey), default: .defaultPopup)
        let plainCombo = HotkeyCombo.parse(store.setting(Store.hotkeyPlainKey), default: .defaultPlain)
        let stackCombo = HotkeyCombo.parse(store.setting(Store.hotkeyStackKey), default: .defaultStack)
        let shotCombo = HotkeyCombo.parse(store.setting(Store.hotkeyShotKey), default: .defaultShot)
        let a = HotkeyManager.shared.register(id: Self.popupHotkeyID, combo: popupCombo) { [weak self] in self?.showPopup() }
        let b = HotkeyManager.shared.register(id: Self.plainHotkeyID, combo: plainCombo) { [weak self] in self?.pasteRecentPlain() }
        _ = HotkeyManager.shared.register(id: Self.stackHotkeyID, combo: stackCombo) { [weak self] in self?.pasteNextFromStack() }
        _ = HotkeyManager.shared.register(id: Self.shotHotkeyID, combo: shotCombo) { Screenshot.capture(.region) }
        openItem?.title = "打开历史 (\(popupCombo.display()))"
        return (a, b)
    }

    func reloadHotkeys() { _ = reloadHotkeysInternal() }
    func suspendHotkeys() { HotkeyManager.shared.unregisterAll() }
    func resumeHotkeys() { reloadHotkeys() }

    func trySetHotkey(popup isPopup: Bool, combo: HotkeyCombo) -> Bool {
        let key = isPopup ? Store.hotkeyPopupKey : Store.hotkeyPlainKey
        let prev = store.setting(key) ?? (isPopup ? HotkeyCombo.defaultPopup : .defaultPlain).serialize()
        store.setSetting(key, combo.serialize())
        let (a, b) = reloadHotkeysInternal()
        if a && b { return true }
        store.setSetting(key, prev)
        reloadHotkeysInternal()
        return false
    }

    func applyAppearance(_ tag: String) { Appearance.apply(tag) }

    // MARK: - paste orchestration

    private func hidePopup() { popup.hide() }

    private func activateAndPaste() {
        let trusted = Paste.ensureAccessibility(prompt: true)
        let prev = previousApp
        DispatchQueue.main.async {
            prev?.activate()
            if trusted {
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.12) { Paste.sendCmdV() }
            }
        }
    }

    func paste(item: ClipItem, plain: Bool) {
        hidePopup()
        monitor.suppress()
        Paste.writeItem(item, plain: plain)
        store.touch(item.id)
        activateAndPaste()
    }

    private func pasteText(_ text: String) {
        hidePopup()
        monitor.suppress()
        Paste.writeText(text)
        activateAndPaste()
    }

    func pasteSnippet(_ s: Snippet) { pasteText(s.content) }

    func pastePassword(_ entry: PasswordEntry) {
        hidePopup()
        guard ensureUnlocked() else { return }
        guard let secret = vault.reveal(entry.id) else { Prompt.info("无法解密该密码"); return }
        pasteText(secret)
    }

    func copy(item: ClipItem) {
        hidePopup()
        monitor.suppress()
        Paste.writeItem(item)
    }

    // MARK: - paste stack

    func addToStack(item: ClipItem) {
        pasteStack.enqueue(item)
        updateStackMenu()
    }

    /// Pastes the next stacked item into the current foreground app (FIFO), then advances.
    private func pasteNextFromStack() {
        guard let item = pasteStack.dequeue() else { return }
        updateStackMenu()
        previousApp = NSWorkspace.shared.frontmostApplication
        monitor.suppress()
        Paste.writeItem(item)
        store.touch(item.id)
        activateAndPaste()
    }

    private func updateStackMenu() {
        stackItem?.title = "粘贴堆栈:\(pasteStack.count) 项"
    }

    @objc private func clearStackAction() {
        pasteStack.clear()
        updateStackMenu()
    }

    func setPinned(_ item: ClipItem, _ pinned: Bool) { store.setPinned(item.id, pinned); popup.model.reload() }
    func delete(item: ClipItem) { store.delete(item.id); popup.model.reload() }
    func moveToList(item: ClipItem, listId: Int64?) { store.assignToList(item.id, listId: listId); popup.model.reload() }

    func newListAndAdd(item: ClipItem) {
        guard let name = Prompt.text("新建列表", "列表名称"), !name.isEmpty else { return }
        let id = store.addList(name: name)
        store.assignToList(item.id, listId: id)
        popup.model.reload()
    }

    func editAndPaste(item: ClipItem) {
        hidePopup()
        guard let edited = Prompt.text("编辑后粘贴", "", value: item.text ?? "", multiline: true) else { return }
        pasteText(edited)
    }

    func transformAndPaste(item: ClipItem, transform: TextTransform) {
        pasteText(transform.apply(item.text ?? ""))
    }

    func openLink(item: ClipItem) {
        hidePopup()
        if let s = item.text?.trimmingCharacters(in: .whitespacesAndNewlines), let url = URL(string: s) {
            NSWorkspace.shared.open(url)
        }
    }

    func revealFile(item: ClipItem) {
        hidePopup()
        let urls = (item.text ?? "").split(separator: "\n").map { URL(fileURLWithPath: String($0)) }
        if !urls.isEmpty { NSWorkspace.shared.activateFileViewerSelecting(urls) }
    }

    func saveImage(item: ClipItem) {
        hidePopup()
        guard let data = item.data else { return }
        let panel = NSSavePanel()
        panel.nameFieldStringValue = "image.png"
        if #available(macOS 11.0, *) { panel.allowedContentTypes = [.png] }
        NSApp.activate(ignoringOtherApps: true)
        if panel.runModal() == .OK, let url = panel.url { try? data.write(to: url) }
    }

    func closePopup() { hidePopup() }

    // MARK: - vault unlock

    private func ensureUnlocked() -> Bool {
        if vault.isUnlocked { return true }
        if !vault.isConfigured { Prompt.info("尚未设置主密码", "请在「设置 → 密码保护」中设置主密码。"); return false }
        while !vault.isUnlocked {
            guard let pw = Prompt.text("解锁密码", "输入主密码:", secure: true) else { return false }
            if !vault.unlock(pw) { Prompt.info("主密码不正确") }
        }
        return true
    }

    // MARK: - status item + menu

    private func setupStatusItem() {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        if let button = statusItem.button {
            let img = NSImage(systemSymbolName: "doc.on.clipboard", accessibilityDescription: "FineClipboard")
            img?.isTemplate = true
            button.image = img
        }

        let menu = NSMenu()
        let popupCombo = HotkeyCombo.parse(store.setting(Store.hotkeyPopupKey), default: .defaultPopup)
        openItem = NSMenuItem(title: "打开历史 (\(popupCombo.display()))", action: #selector(openPopupAction), keyEquivalent: "")
        openItem.target = self
        menu.addItem(openItem)

        let settings = NSMenuItem(title: "设置…", action: #selector(openSettingsAction), keyEquivalent: "")
        settings.target = self
        menu.addItem(settings)

        let syncItem = NSMenuItem(title: "云同步…", action: #selector(openSyncAction), keyEquivalent: "")
        syncItem.target = self
        menu.addItem(syncItem)

        pauseItem = NSMenuItem(title: "暂停记录(隐私模式)", action: #selector(togglePauseAction), keyEquivalent: "")
        pauseItem.target = self
        menu.addItem(pauseItem)

        let lock = NSMenuItem(title: "锁定密码", action: #selector(lockVaultAction), keyEquivalent: "")
        lock.target = self
        menu.addItem(lock)

        let stackCombo = HotkeyCombo.parse(store.setting(Store.hotkeyStackKey), default: .defaultStack)
        stackItem = NSMenuItem(title: "粘贴堆栈:0 项", action: nil, keyEquivalent: "")
        stackItem.isEnabled = false
        menu.addItem(stackItem)
        let clearStack = NSMenuItem(title: "清空粘贴堆栈 (下一项 \(stackCombo.display()))", action: #selector(clearStackAction), keyEquivalent: "")
        clearStack.target = self
        menu.addItem(clearStack)

        let shotCombo = HotkeyCombo.parse(store.setting(Store.hotkeyShotKey), default: .defaultShot)
        let shotMenu = NSMenu()
        let shotRegion = NSMenuItem(title: "截取区域 (\(shotCombo.display()))", action: #selector(shotRegionAction), keyEquivalent: "")
        shotRegion.target = self
        shotMenu.addItem(shotRegion)
        let shotWindow = NSMenuItem(title: "截取窗口", action: #selector(shotWindowAction), keyEquivalent: "")
        shotWindow.target = self
        shotMenu.addItem(shotWindow)
        let shotFull = NSMenuItem(title: "全屏截图", action: #selector(shotFullAction), keyEquivalent: "")
        shotFull.target = self
        shotMenu.addItem(shotFull)
        let shotItem = NSMenuItem(title: "截图", action: nil, keyEquivalent: "")
        shotItem.submenu = shotMenu
        menu.addItem(shotItem)

        startupItem = NSMenuItem(title: "开机自启", action: #selector(toggleStartupAction), keyEquivalent: "")
        startupItem.target = self
        startupItem.state = LoginItem.isEnabled ? .on : .off
        menu.addItem(startupItem)

        updateItem = NSMenuItem(title: "检查更新…", action: #selector(checkUpdateAction), keyEquivalent: "")
        updateItem.target = self
        menu.addItem(updateItem)

        menu.addItem(.separator())
        let quit = NSMenuItem(title: "退出", action: #selector(quitAction), keyEquivalent: "q")
        quit.target = self
        menu.addItem(quit)

        statusItem.menu = menu
    }

    @objc private func openPopupAction() { showPopup() }
    @objc private func openSettingsAction() { showSettings() }
    @objc private func lockVaultAction() { vault.lock() }
    @objc private func quitAction() { NSApp.terminate(nil) }
    @objc private func shotRegionAction() { Screenshot.capture(.region) }
    @objc private func shotWindowAction() { Screenshot.capture(.window) }
    @objc private func shotFullAction() { Screenshot.capture(.fullscreen) }

    @objc private func togglePauseAction() {
        monitor.paused.toggle()
        pauseItem.state = monitor.paused ? .on : .off
    }

    @objc private func toggleStartupAction() {
        let next = !(startupItem.state == .on)
        LoginItem.setEnabled(next)
        startupItem.state = LoginItem.isEnabled ? .on : .off
    }

    @objc private func checkUpdateAction() {
        if let url = updateURL, let u = URL(string: url) { NSWorkspace.shared.open(u); return }
        Updater.check { [weak self] result in self?.applyUpdate(result, silent: false) }
    }

    private func applyUpdate(_ result: Updater.CheckResult, silent: Bool) {
        switch result {
        case .available(let update):
            updateURL = update.url
            updateItem.title = "⬇ 下载新版本 \(update.version)"
            if !silent { Prompt.info("有新版本 \(update.version)", "点击菜单栏的「下载新版本」即可打开下载页。") }
        case .upToDate:
            if !silent { Prompt.info("当前已是最新版本") }
        case .failed:
            if !silent { Prompt.info("检查更新失败", "请检查网络后重试。") }
        }
    }

    // MARK: - windows

    private func showSettings() {
        if settingsWC == nil {
            let view = SettingsView(
                host: self,
                onManageSnippets: { [weak self] in self?.showSnippets() },
                onManageLists: { [weak self] in self?.showLists() },
                onManagePasswords: { [weak self] in self?.showPasswords() },
                onMasterPassword: { [weak self] in self?.setOrChangeMaster() },
                masterTitle: { [weak self] in (self?.vault.isConfigured ?? false) ? "修改主密码…" : "设置主密码…" },
                model: SettingsModel(host: self))
            settingsWC = WindowFactory.make(title: "FineClipboard 设置", width: 480, height: 640, root: view)
        }
        activate(settingsWC)
    }

    private func showSnippets() {
        if snippetsWC == nil { snippetsWC = WindowFactory.make(title: "常用片段", width: 420, height: 360, root: SnippetsView(store: store)) }
        activate(snippetsWC)
    }

    private func showLists() {
        if listsWC == nil { listsWC = WindowFactory.make(title: "列表", width: 380, height: 320, root: ListsView(store: store)) }
        activate(listsWC)
    }

    private func showPasswords() {
        guard ensureUnlocked() else { return }
        if passwordsWC == nil { passwordsWC = WindowFactory.make(title: "密码", width: 440, height: 360, root: PasswordsView(vault: vault)) }
        activate(passwordsWC)
    }

    private func activate(_ wc: NSWindowController?) {
        wc?.showWindow(nil)
        wc?.window?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    private func setOrChangeMaster() {
        if vault.isConfigured {
            guard let v = Prompt.secureFields("修改主密码", ["当前主密码", "新主密码", "确认新主密码"]) else { return }
            guard !v[1].isEmpty, v[1] == v[2] else { Prompt.info("两次输入的新密码不一致或为空"); return }
            if vault.changeMasterPassword(old: v[0], new: v[1]) { Prompt.info("主密码已更新") }
            else { Prompt.info("当前主密码不正确") }
        } else {
            guard let v = Prompt.secureFields("设置主密码", ["主密码", "确认主密码"]) else { return }
            guard !v[0].isEmpty, v[0] == v[1] else { Prompt.info("两次输入不一致或为空"); return }
            vault.setMasterPassword(v[0])
            Prompt.info("主密码已设置", "请牢记主密码,忘记将无法找回已保存的密码。")
        }
    }

    // MARK: - first run

    private func showFirstRun() {
        if ProcessInfo.processInfo.environment["FINECLIP_NO_FIRSTRUN"] == "1" {
            return // smoke-test hook: don't block on the welcome modal
        }
        if store.setting(Store.firstRunKey) == "1" {
            return
        }
        store.setSetting(Store.firstRunKey, "1")
        let combo = HotkeyCombo.parse(store.setting(Store.hotkeyPopupKey), default: .defaultPopup)
        Prompt.info("FineClipboard 已启动",
            "按 \(combo.display()) 打开剪贴板历史。\n程序常驻菜单栏(顶部栏的剪贴板图标)。\n\n首次使用请在接下来的「辅助功能」设置里允许 FineClipboard,以便自动粘贴。")
        Paste.ensureAccessibility(prompt: true)
    }
}
