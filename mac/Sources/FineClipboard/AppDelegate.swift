import Cocoa
import SwiftUI

final class AppDelegate: NSObject, NSApplicationDelegate, PopupHost, AppControl {
    let store = Store()
    lazy var vault = Vault(store)

    private let monitor = ClipboardMonitor()
    private lazy var sync = SyncEngine(store)
    private var syncTimer: Timer?
    private var syncing = false
    private var syncWC: NSWindowController?
    private lazy var popup = PopupController(model: PopupModel(store: store, vault: vault))

    private var statusItem: NSStatusItem!
    private var updateItem: NSMenuItem!
    private var updateURL: String?
    private var pendingScreenshotPreview = false
    private var pendingScreenshotPreviewUntil = Date.distantPast

    private var previousApp: NSRunningApplication?
    private var purgeTimer: Timer?

    private var settingsWC: NSWindowController?
    private var screenshotPreviewWC: NSWindowController?

    private static let popupHotkeyID: UInt32 = 1
    private static let plainHotkeyID: UInt32 = 2
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

        if pendingScreenshotPreview, Date() > pendingScreenshotPreviewUntil {
            pendingScreenshotPreview = false
        }

        if pendingScreenshotPreview, cap.kind == .image, let png = cap.data {
            pendingScreenshotPreview = false
            showScreenshotPreview(data: png)
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
            pasteText(text, waitForShortcutRelease: true)
            store.touch(item.id)
        }
    }

    @discardableResult
    private func reloadHotkeysInternal() -> (Bool, Bool, Bool) {
        HotkeyManager.shared.unregisterAll()
        let popupCombo = HotkeyCombo.parse(store.setting(Store.hotkeyPopupKey), default: .defaultPopup)
        let plainCombo = HotkeyCombo.parse(store.setting(Store.hotkeyPlainKey), default: .defaultPlain)
        let shotCombo = HotkeyCombo.parse(store.setting(Store.hotkeyShotKey), default: .defaultShot)
        let a = HotkeyManager.shared.register(id: Self.popupHotkeyID, combo: popupCombo) { [weak self] in self?.showPopup() }
        let b = HotkeyManager.shared.register(id: Self.plainHotkeyID, combo: plainCombo) { [weak self] in self?.pasteRecentPlain() }
        let c = HotkeyManager.shared.register(id: Self.shotHotkeyID, combo: shotCombo) { [weak self] in self?.captureScreenshot(.region) }
        return (a, b, c)
    }

    func reloadHotkeys() { _ = reloadHotkeysInternal() }
    func suspendHotkeys() { HotkeyManager.shared.unregisterAll() }
    func resumeHotkeys() { reloadHotkeys() }

    func trySetHotkey(popup isPopup: Bool, combo: HotkeyCombo) -> Bool {
        trySetHotkey(target: isPopup ? "popup" : "plain", combo: combo)
    }

    func trySetHotkey(target: String, combo: HotkeyCombo) -> Bool {
        let key: String
        let def: HotkeyCombo
        switch target {
        case "popup": key = Store.hotkeyPopupKey; def = .defaultPopup
        case "plain": key = Store.hotkeyPlainKey; def = .defaultPlain
        case "shot": key = Store.hotkeyShotKey; def = .defaultShot
        default: return false
        }
        let prev = store.setting(key) ?? def.serialize()
        store.setSetting(key, combo.serialize())
        let (a, b, c) = reloadHotkeysInternal()
        if a && b && c { return true }
        store.setSetting(key, prev)
        reloadHotkeysInternal()
        return false
    }

    func applyAppearance(_ tag: String) { Appearance.apply(tag) }
    func setRecordingPaused(_ paused: Bool) { monitor.paused = paused }
    var isRecordingPaused: Bool { monitor.paused }
    func showSyncSettings() { showSync() }
    func lockVault() { vault.lock() }
    func screenshotHotkeyDisplay() -> String {
        HotkeyCombo.parse(store.setting(Store.hotkeyShotKey), default: .defaultShot).display()
    }

    // MARK: - paste orchestration

    private func hidePopup() { popup.hide() }

    private func activateAndPaste(waitForShortcutRelease: Bool = false) {
        let trusted = Paste.ensureAccessibility(prompt: true)
        let prev = previousApp
        DispatchQueue.main.async {
            prev?.activate()
            if trusted {
                DispatchQueue.main.asyncAfter(deadline: .now() + 0.12) { [weak self] in
                    if waitForShortcutRelease { self?.sendPasteWhenModifiersReleased() }
                    else { Paste.sendCmdV() }
                }
            }
        }
    }

    private func sendPasteWhenModifiersReleased(retries: Int = 150) {
        let modifiers: CGEventFlags = [.maskCommand, .maskShift, .maskAlternate, .maskControl]
        let held = !CGEventSource.flagsState(.combinedSessionState).intersection(modifiers).isEmpty
        if !held || retries == 0 {
            Paste.sendCmdV()
            return
        }
        DispatchQueue.main.asyncAfter(deadline: .now() + 0.01) { [weak self] in
            self?.sendPasteWhenModifiersReleased(retries: retries - 1)
        }
    }

    func paste(item: ClipItem, plain: Bool) {
        hidePopup()
        monitor.suppress()
        Paste.writeItem(item, plain: plain)
        store.touch(item.id)
        activateAndPaste()
    }

    private func pasteText(_ text: String, waitForShortcutRelease: Bool = false) {
        hidePopup()
        monitor.suppress()
        Paste.writeText(text)
        activateAndPaste(waitForShortcutRelease: waitForShortcutRelease)
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

    func setPinned(_ item: ClipItem, _ pinned: Bool) { store.setPinned(item.id, pinned); popup.model.reload() }
    func delete(item: ClipItem) { store.delete(item.id); popup.model.reload() }
    func clearHistoryKeepingFavorites() {
        if Prompt.confirm("确定清空历史吗?", "收藏项会保留。") {
            store.clear(keepPinned: true)
            popup.model.reload()
        }
    }
    func moveToList(item: ClipItem, listId: Int64?) { store.assignToList(item.id, listId: listId); popup.model.reload() }

    func newListAndAdd(item: ClipItem) {
        guard let name = Prompt.text("新建列表", "列表名称"), !name.isEmpty else { return }
        let id = store.addList(name: name)
        store.assignToList(item.id, listId: id)
        popup.model.reload()
    }

    func createList() {
        guard let name = Prompt.text("新建列表", "列表名称")?.trimmingCharacters(in: .whitespacesAndNewlines),
              !name.isEmpty else { return }
        let id = store.addList(name: name)
        popup.model.reload()
        popup.model.select(tab: .list(id))
    }

    func deleteList(_ list: ClipList) {
        guard Prompt.confirm("确定删除列表“\(list.name)”吗？", "列表中的项目会回到全部历史。") else { return }
        store.deleteList(list.id)
        popup.model.reload()
        popup.model.select(tab: .all)
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
            button.image = makeStatusIcon()
        }

        let menu = NSMenu()
        let settings = NSMenuItem(title: "设置…", action: #selector(openSettingsAction), keyEquivalent: "")
        settings.target = self
        menu.addItem(settings)

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

        let clearHistory = NSMenuItem(title: "清空剪贴板历史…", action: #selector(clearHistoryAction), keyEquivalent: "")
        clearHistory.target = self
        menu.addItem(clearHistory)

        updateItem = NSMenuItem(title: "检查更新…", action: #selector(checkUpdateAction), keyEquivalent: "")
        updateItem.target = self
        menu.addItem(updateItem)

        menu.addItem(.separator())
        let quit = NSMenuItem(title: "退出", action: #selector(quitAction), keyEquivalent: "q")
        quit.target = self
        menu.addItem(quit)

        statusItem.menu = menu
    }

    private func makeStatusIcon() -> NSImage {
        let image = NSImage(size: NSSize(width: 18, height: 18))
        image.lockFocus()
        NSColor.labelColor.setStroke()
        let stroke = NSBezierPath()
        stroke.lineWidth = 1.8
        stroke.lineCapStyle = .round
        stroke.lineJoinStyle = .round
        stroke.move(to: NSPoint(x: 6, y: 14))
        stroke.line(to: NSPoint(x: 12, y: 14))
        stroke.move(to: NSPoint(x: 5, y: 12))
        stroke.curve(to: NSPoint(x: 4, y: 10), controlPoint1: NSPoint(x: 4.4, y: 12), controlPoint2: NSPoint(x: 4, y: 11.4))
        stroke.line(to: NSPoint(x: 4, y: 4))
        stroke.curve(to: NSPoint(x: 6, y: 2), controlPoint1: NSPoint(x: 4, y: 2.8), controlPoint2: NSPoint(x: 4.8, y: 2))
        stroke.line(to: NSPoint(x: 12, y: 2))
        stroke.curve(to: NSPoint(x: 14, y: 4), controlPoint1: NSPoint(x: 13.2, y: 2), controlPoint2: NSPoint(x: 14, y: 2.8))
        stroke.line(to: NSPoint(x: 14, y: 10))
        stroke.curve(to: NSPoint(x: 13, y: 12), controlPoint1: NSPoint(x: 14, y: 11.4), controlPoint2: NSPoint(x: 13.6, y: 12))
        stroke.stroke()

        let lines = NSBezierPath()
        lines.lineWidth = 1.3
        lines.lineCapStyle = .round
        lines.move(to: NSPoint(x: 7, y: 9))
        lines.line(to: NSPoint(x: 11, y: 9))
        lines.move(to: NSPoint(x: 7, y: 6))
        lines.line(to: NSPoint(x: 10, y: 6))
        lines.stroke()
        image.unlockFocus()
        image.isTemplate = true
        return image
    }

    @objc private func openSettingsAction() { showSettings() }
    @objc private func quitAction() { NSApp.terminate(nil) }
    @objc private func shotRegionAction() { captureScreenshot(.region) }
    @objc private func shotWindowAction() { captureScreenshot(.window) }
    @objc private func shotFullAction() { captureScreenshot(.fullscreen) }
    @objc private func clearHistoryAction() { clearHistoryKeepingFavorites() }

    private func captureScreenshot(_ mode: Screenshot.Mode) {
        pendingScreenshotPreview = true
        pendingScreenshotPreviewUntil = Date().addingTimeInterval(120)
        Screenshot.capture(mode)
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
                onSetPassword: { [weak self] in self?.setMasterPassword() },
                model: SettingsModel(host: self))
            settingsWC = WindowFactory.make(title: "FineClipboard 设置", width: 560, height: 540, root: view)
        }
        activate(settingsWC)
    }

    private func showScreenshotPreview(data: Data) {
        screenshotPreviewWC?.close()
        screenshotPreviewWC = WindowFactory.make(
            title: "截图预览",
            width: 760,
            height: 560,
            root: ScreenshotPreviewView(data: data))
        activate(screenshotPreviewWC)
    }

    private func activate(_ wc: NSWindowController?) {
        wc?.showWindow(nil)
        wc?.window?.makeKeyAndOrderFront(nil)
        NSApp.activate(ignoringOtherApps: true)
    }

    private func setMasterPassword() {
        guard !vault.isConfigured else { return }
        guard let v = Prompt.secureFields("设置主密码", ["主密码", "确认主密码"]) else { return }
        guard !v[0].isEmpty, v[0] == v[1] else { Prompt.info("两次输入不一致或为空"); return }
        vault.setMasterPassword(v[0])
        Prompt.info("主密码已设置", "请牢记主密码,忘记将无法找回已保存的密码。")
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
