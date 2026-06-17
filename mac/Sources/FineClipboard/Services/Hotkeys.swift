import Cocoa
import Carbon

/// A global hotkey: Carbon modifier mask + hardware key code (same codes as `NSEvent.keyCode`).
struct HotkeyCombo: Equatable {
    var modifiers: UInt32   // cmdKey | shiftKey | optionKey | controlKey
    var keyCode: UInt32

    static let defaultPopup = HotkeyCombo(modifiers: UInt32(cmdKey | shiftKey), keyCode: UInt32(kVK_ANSI_V))
    static let defaultPlain = HotkeyCombo(modifiers: UInt32(cmdKey | shiftKey), keyCode: UInt32(kVK_ANSI_B))
    static let defaultShot = HotkeyCombo(modifiers: UInt32(cmdKey | shiftKey), keyCode: UInt32(kVK_ANSI_A))

    func serialize() -> String { "\(modifiers):\(keyCode)" }

    static func parse(_ s: String?, default def: HotkeyCombo) -> HotkeyCombo {
        guard let s, case let parts = s.split(separator: ":"), parts.count == 2,
              let m = UInt32(parts[0]), let k = UInt32(parts[1]) else { return def }
        return HotkeyCombo(modifiers: m, keyCode: k)
    }

    /// Build a combo from a recorded key event; requires at least one modifier.
    static func from(event: NSEvent) -> HotkeyCombo? {
        var mods: UInt32 = 0
        let f = event.modifierFlags
        if f.contains(.command) { mods |= UInt32(cmdKey) }
        if f.contains(.shift) { mods |= UInt32(shiftKey) }
        if f.contains(.option) { mods |= UInt32(optionKey) }
        if f.contains(.control) { mods |= UInt32(controlKey) }
        if mods == 0 { return nil }
        return HotkeyCombo(modifiers: mods, keyCode: UInt32(event.keyCode))
    }

    func display() -> String {
        var s = ""
        if modifiers & UInt32(controlKey) != 0 { s += "⌃" }
        if modifiers & UInt32(optionKey) != 0 { s += "⌥" }
        if modifiers & UInt32(shiftKey) != 0 { s += "⇧" }
        if modifiers & UInt32(cmdKey) != 0 { s += "⌘" }
        return s + HotkeyCombo.keyLabel(keyCode)
    }

    private static let labels: [UInt32: String] = [
        0: "A", 1: "S", 2: "D", 3: "F", 4: "H", 5: "G", 6: "Z", 7: "X", 8: "C", 9: "V",
        11: "B", 12: "Q", 13: "W", 14: "E", 15: "R", 16: "Y", 17: "T", 32: "U", 34: "I",
        31: "O", 35: "P", 37: "L", 38: "J", 40: "K", 45: "N", 46: "M", 30: "]", 33: "[",
        18: "1", 19: "2", 20: "3", 21: "4", 23: "5", 22: "6", 26: "7", 28: "8", 25: "9", 29: "0",
        36: "↩", 48: "⇥", 49: "Space", 51: "⌫", 53: "⎋", 76: "⌅", 117: "⌦",
        123: "←", 124: "→", 125: "↓", 126: "↑", 27: "-", 24: "=", 41: ";", 39: "'",
        43: ",", 47: ".", 44: "/", 42: "\\", 50: "`",
    ]

    static func keyLabel(_ code: UInt32) -> String { labels[code] ?? "Key\(code)" }
}

/// Registers system-wide hotkeys via Carbon and routes presses to Swift callbacks.
final class HotkeyManager {
    static let shared = HotkeyManager()

    private var refs: [UInt32: EventHotKeyRef] = [:]
    fileprivate var handlers: [UInt32: () -> Void] = [:]
    private var installed = false
    private let signature: OSType = 0x46434C42 // 'FCLB'

    @discardableResult
    func register(id: UInt32, combo: HotkeyCombo, handler: @escaping () -> Void) -> Bool {
        installHandlerIfNeeded()
        unregister(id: id)
        var ref: EventHotKeyRef?
        let hkID = EventHotKeyID(signature: signature, id: id)
        let status = RegisterEventHotKey(combo.keyCode, combo.modifiers, hkID, GetEventDispatcherTarget(), 0, &ref)
        if status == noErr, let ref {
            refs[id] = ref
            handlers[id] = handler
            return true
        }
        return false
    }

    func unregister(id: UInt32) {
        if let ref = refs[id] { UnregisterEventHotKey(ref); refs[id] = nil }
        handlers[id] = nil
    }

    func unregisterAll() { for id in Array(refs.keys) { unregister(id: id) } }

    private func installHandlerIfNeeded() {
        guard !installed else { return }
        installed = true
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        InstallEventHandler(GetEventDispatcherTarget(), { (_, event, _) -> OSStatus in
            var hkID = EventHotKeyID()
            GetEventParameter(event, EventParamName(kEventParamDirectObject), EventParamType(typeEventHotKeyID),
                              nil, MemoryLayout<EventHotKeyID>.size, nil, &hkID)
            HotkeyManager.shared.handlers[hkID.id]?()
            return noErr
        }, 1, &spec, nil, nil)
    }
}
