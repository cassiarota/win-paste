import Foundation

/// Screen capture via the built-in macOS `screencapture` tool. Each mode copies the result
/// to the clipboard (`-c`), so the captured image flows into history like any other copy
/// (and gets OCR'd + can be saved to a file via the image right-click menu).
enum Screenshot {
    enum Mode { case region, window, fullscreen }

    static func capture(_ mode: Mode) {
        let task = Process()
        task.executableURL = URL(fileURLWithPath: "/usr/sbin/screencapture")
        switch mode {
        // -i interactive: drag a rectangle (Space toggles window mode); -c to clipboard.
        case .region: task.arguments = ["-i", "-c"]
        // -W starts directly in magnetic window-selection mode.
        case .window: task.arguments = ["-i", "-W", "-c"]
        // No -i: capture the main display immediately.
        case .fullscreen: task.arguments = ["-c"]
        }
        try? task.run()
    }
}
