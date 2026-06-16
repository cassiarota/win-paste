import Cocoa

/// Polls the general pasteboard (macOS has no change event) and reports new captures.
/// Mirrors the Windows `ClipboardMonitor`: pause (privacy mode), suppression (so our own
/// pastes aren't re-captured), and source-app exclusion.
/// One captured clipboard entry handed to the app.
struct Capture {
    let kind: ClipKind
    let text: String?
    let data: Data?
    let preview: String
    let source: String?
    var html: String? = nil
    var rtf: String? = nil
}

final class ClipboardMonitor {
    var onCapture: ((Capture) -> Void)?
    var shouldSkipSource: ((String?) -> Bool)?
    var paused = false

    private var lastChangeCount = 0
    private var suppressUntil = Date.distantPast
    private var timer: Timer?

    func start() {
        lastChangeCount = NSPasteboard.general.changeCount
        let t = Timer(timeInterval: 0.4, repeats: true) { [weak self] _ in self?.poll() }
        RunLoop.main.add(t, forMode: .common)
        timer = t
    }

    func stop() { timer?.invalidate(); timer = nil }

    /// Ignore captures for a short window (used right before we write to the pasteboard ourselves).
    func suppress(_ seconds: TimeInterval = 1.0) { suppressUntil = Date().addingTimeInterval(seconds) }

    private func poll() {
        let pb = NSPasteboard.general
        let cc = pb.changeCount
        guard cc != lastChangeCount else { return }
        lastChangeCount = cc

        if paused || Date() < suppressUntil { return }
        let source = NSWorkspace.shared.frontmostApplication?.localizedName
        if shouldSkipSource?(source) == true { return }

        // Priority: files -> text -> image (mirrors typical clipboard managers).
        if let urls = pb.readObjects(forClasses: [NSURL.self], options: [.urlReadingFileURLsOnly: true]) as? [URL], !urls.isEmpty {
            let paths = urls.map { $0.path }.joined(separator: "\n")
            let names = urls.map { $0.lastPathComponent }.joined(separator: ", ")
            onCapture?(Capture(kind: .files, text: paths, data: nil, preview: names, source: source))
            return
        }
        if let s = pb.string(forType: .string), !s.isEmpty {
            let html = ClipboardMonitor.nonEmpty(pb.string(forType: .html))
            let rtf = ClipboardMonitor.nonEmpty(pb.data(forType: .rtf).flatMap { String(data: $0, encoding: .utf8) })
            onCapture?(Capture(kind: .text, text: s, data: nil, preview: ClipboardMonitor.preview(s),
                               source: source, html: html, rtf: rtf))
            return
        }
        if let png = ClipboardMonitor.readImagePNG(pb) {
            onCapture?(Capture(kind: .image, text: nil, data: png, preview: "🖼 图片", source: source))
            return
        }
    }

    private static func nonEmpty(_ s: String?) -> String? {
        guard let s, !s.isEmpty else { return nil }
        return s
    }

    static func preview(_ text: String) -> String {
        let line = text.trimmingCharacters(in: .whitespacesAndNewlines)
            .replacingOccurrences(of: "\n", with: " ")
            .replacingOccurrences(of: "\r", with: " ")
        return line.count > 100 ? String(line.prefix(100)) + "…" : line
    }

    /// Read the pasteboard image and normalise it to PNG bytes.
    static func readImagePNG(_ pb: NSPasteboard) -> Data? {
        guard let data = pb.data(forType: .tiff) ?? pb.data(forType: .png),
              let rep = NSBitmapImageRep(data: data) else { return nil }
        return rep.representation(using: .png, properties: [:])
    }
}
