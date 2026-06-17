import Cocoa
import SwiftUI

/// What the settings/recorder UI needs from the app.
protocol AppControl: AnyObject {
    var store: Store { get }
    var vault: Vault { get }
    func reloadHotkeys()
    func suspendHotkeys()
    func resumeHotkeys()
    func trySetHotkey(popup: Bool, combo: HotkeyCombo) -> Bool
    func applyAppearance(_ tag: String)
    var isRecordingPaused: Bool { get }
    func setRecordingPaused(_ paused: Bool)
    func showSyncSettings()
    func lockVault()
    func screenshotHotkeyDisplay() -> String
}

/// Simple modal prompts built on NSAlert (used for the small editors).
enum Prompt {
    static func info(_ title: String, _ message: String = "") {
        let a = NSAlert(); a.messageText = title; a.informativeText = message
        a.addButton(withTitle: "好")
        NSApp.activate(ignoringOtherApps: true)
        a.runModal()
    }

    static func confirm(_ title: String, _ message: String = "") -> Bool {
        let a = NSAlert(); a.messageText = title; a.informativeText = message
        a.addButton(withTitle: "确定"); a.addButton(withTitle: "取消")
        NSApp.activate(ignoringOtherApps: true)
        return a.runModal() == .alertFirstButtonReturn
    }

    /// Single-line / multiline / secure text input.
    static func text(_ title: String, _ message: String = "", value: String = "",
                     secure: Bool = false, multiline: Bool = false) -> String? {
        let a = NSAlert(); a.messageText = title; a.informativeText = message
        a.addButton(withTitle: "确定"); a.addButton(withTitle: "取消")
        let width: CGFloat = 300
        let getter: () -> String
        if multiline {
            let tv = NSTextView(frame: NSRect(x: 0, y: 0, width: width, height: 96))
            tv.string = value; tv.font = .systemFont(ofSize: 12); tv.isRichText = false
            let scroll = NSScrollView(frame: NSRect(x: 0, y: 0, width: width, height: 96))
            scroll.documentView = tv; scroll.hasVerticalScroller = true; scroll.borderType = .bezelBorder
            a.accessoryView = scroll
            getter = { tv.string }
        } else {
            let tf: NSTextField = secure ? NSSecureTextField(frame: NSRect(x: 0, y: 0, width: width, height: 24))
                                         : NSTextField(frame: NSRect(x: 0, y: 0, width: width, height: 24))
            tf.stringValue = value
            a.accessoryView = tf
            a.window.initialFirstResponder = tf
            getter = { tf.stringValue }
        }
        NSApp.activate(ignoringOtherApps: true)
        return a.runModal() == .alertFirstButtonReturn ? getter() : nil
    }

    /// One dialog with several labelled secure fields (e.g. old / new / confirm).
    static func secureFields(_ title: String, _ labels: [String]) -> [String]? {
        let a = NSAlert(); a.messageText = title
        a.addButton(withTitle: "确定"); a.addButton(withTitle: "取消")
        let width: CGFloat = 280
        let container = NSStackView()
        container.orientation = .vertical
        container.alignment = .leading
        container.spacing = 6
        container.translatesAutoresizingMaskIntoConstraints = true
        container.frame = NSRect(x: 0, y: 0, width: width, height: CGFloat(labels.count) * 46)
        var fields: [NSSecureTextField] = []
        for label in labels {
            let l = NSTextField(labelWithString: label); l.font = .systemFont(ofSize: 11)
            let f = NSSecureTextField()
            f.widthAnchor.constraint(equalToConstant: width).isActive = true
            fields.append(f)
            let row = NSStackView(views: [l, f]); row.orientation = .vertical
            row.alignment = .leading; row.spacing = 2
            container.addArrangedSubview(row)
        }
        a.accessoryView = container
        a.window.initialFirstResponder = fields.first
        NSApp.activate(ignoringOtherApps: true)
        return a.runModal() == .alertFirstButtonReturn ? fields.map { $0.stringValue } : nil
    }

    /// Two fields: name + secret (for password entries).
    static func nameAndSecret(_ title: String, name: String = "", secret: String = "") -> (String, String)? {
        let a = NSAlert(); a.messageText = title
        a.addButton(withTitle: "确定"); a.addButton(withTitle: "取消")
        let width: CGFloat = 280
        let nameLabel = NSTextField(labelWithString: "名称"); nameLabel.font = .systemFont(ofSize: 11)
        let nameField = NSTextField(); nameField.stringValue = name
        nameField.widthAnchor.constraint(equalToConstant: width).isActive = true
        let secLabel = NSTextField(labelWithString: "密码"); secLabel.font = .systemFont(ofSize: 11)
        let secField = NSSecureTextField(); secField.stringValue = secret
        secField.widthAnchor.constraint(equalToConstant: width).isActive = true
        let container = NSStackView(views: [nameLabel, nameField, secLabel, secField])
        container.orientation = .vertical; container.alignment = .leading; container.spacing = 4
        container.translatesAutoresizingMaskIntoConstraints = true
        container.frame = NSRect(x: 0, y: 0, width: width, height: 110)
        a.accessoryView = container
        a.window.initialFirstResponder = nameField
        NSApp.activate(ignoringOtherApps: true)
        if a.runModal() == .alertFirstButtonReturn { return (nameField.stringValue, secField.stringValue) }
        return nil
    }
}

/// Builds a titled window hosting a SwiftUI view.
enum WindowFactory {
    static func make(title: String, width: CGFloat, height: CGFloat, root: some View) -> NSWindowController {
        let hosting = NSHostingController(rootView: root)
        let win = NSWindow(contentViewController: hosting)
        win.title = title
        win.styleMask = [.titled, .closable]
        win.setContentSize(NSSize(width: width, height: height))
        win.center()
        return NSWindowController(window: win)
    }
}
