import Cocoa
import ApplicationServices

/// Pasteboard writing + synthesized Cmd+V injection (needs Accessibility permission).
enum Paste {
    static func writeText(_ s: String) {
        let pb = NSPasteboard.general
        pb.clearContents()
        pb.setString(s, forType: .string)
    }

    static func writeItem(_ item: ClipItem, plain: Bool = false) {
        let pb = NSPasteboard.general
        pb.clearContents()
        switch item.kind {
        case .text:
            if !plain && item.hasRichText {
                // Keep formatting: declare plain + HTML + RTF so the target app picks its best.
                pb.declareTypes([.string, .html, .rtf], owner: nil)
                pb.setString(item.text ?? "", forType: .string)
                if let html = item.html { pb.setString(html, forType: .html) }
                if let rtf = item.rtf, let data = rtf.data(using: .utf8) { pb.setData(data, forType: .rtf) }
            } else {
                pb.setString(item.text ?? "", forType: .string)
            }
        case .files:
            let urls = (item.text ?? "").split(separator: "\n").map { URL(fileURLWithPath: String($0)) }
            if !urls.isEmpty { pb.writeObjects(urls as [NSURL]) }
        case .image:
            if let data = item.data, let img = NSImage(data: data) {
                pb.writeObjects([img])
            }
        }
    }

    /// Whether this row's content can be dragged/pasted as a string.
    static func dragText(_ item: ClipItem) -> String? {
        switch item.kind {
        case .text, .files: return item.text
        case .image: return nil
        }
    }

    /// Synthesize a Cmd+V key press into the frontmost app.
    static func sendCmdV() {
        let src = CGEventSource(stateID: .combinedSessionState)
        let v = CGKeyCode(9) // kVK_ANSI_V
        let down = CGEvent(keyboardEventSource: src, virtualKey: v, keyDown: true)
        down?.flags = .maskCommand
        let up = CGEvent(keyboardEventSource: src, virtualKey: v, keyDown: false)
        up?.flags = .maskCommand
        down?.post(tap: .cghidEventTap)
        up?.post(tap: .cghidEventTap)
    }

    static var hasAccessibility: Bool { AXIsProcessTrusted() }

    /// Returns true if already trusted; otherwise (optionally) shows the system prompt.
    @discardableResult
    static func ensureAccessibility(prompt: Bool) -> Bool {
        if AXIsProcessTrusted() { return true }
        if prompt {
            let key = kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String
            _ = AXIsProcessTrustedWithOptions([key: true] as CFDictionary)
        }
        return false
    }
}
