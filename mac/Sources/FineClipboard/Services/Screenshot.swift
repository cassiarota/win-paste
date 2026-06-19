import AppKit
import CoreGraphics

/// Cross-platform-parity screen capture: smart window/title-bar/display snapping, manual region
/// selection, full-screen crosshairs, pixel coordinates, RGB inspection and a magnifier.
enum Screenshot {
    enum Mode { case region, window, fullscreen }
    private static var active: SmartScreenshotController?

    static func capture(_ mode: Mode, onCancel: (() -> Void)? = nil) {
        if mode == .fullscreen {
            let task = Process()
            task.executableURL = URL(fileURLWithPath: "/usr/sbin/screencapture")
            task.arguments = ["-c"]
            do { try task.run() } catch { onCancel?() }
            return
        }

        let controller = SmartScreenshotController { accepted in
            active = nil
            if !accepted { onCancel?() }
        }
        active = controller
        if !controller.start() {
            active = nil
            onCancel?()
        }
    }
}

private final class SmartScreenshotController {
    private var window: NSWindow?
    private let completion: (Bool) -> Void
    private var finished = false

    init(completion: @escaping (Bool) -> Void) { self.completion = completion }

    func start() -> Bool {
        guard let cgImage = CGWindowListCreateImage(.infinite, .optionOnScreenOnly, kCGNullWindowID, [.bestResolution]) else {
            return false
        }
        let screens = NSScreen.screens
        guard var frame = screens.first?.frame else { return false }
        for screen in screens.dropFirst() { frame = frame.union(screen.frame) }
        let image = NSImage(cgImage: cgImage, size: frame.size)
        let candidates = Self.windowRects(mainTop: screens.first?.frame.maxY ?? frame.maxY)

        let panel = SmartScreenshotPanel(contentRect: frame, styleMask: [.borderless], backing: .buffered, defer: false)
        panel.level = .screenSaver
        panel.isOpaque = true
        panel.backgroundColor = .black
        panel.hasShadow = false
        panel.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        panel.acceptsMouseMovedEvents = true
        let view = SmartScreenshotView(image: image, globalFrame: frame, candidates: candidates) { [weak self] data in
            guard let self else { return }
            if let data {
                let pasteboard = NSPasteboard.general
                pasteboard.clearContents()
                pasteboard.setData(data, forType: .png)
                self.finish(true)
            } else {
                self.finish(false)
            }
        }
        panel.contentView = view
        window = panel
        NSApp.activate(ignoringOtherApps: true)
        panel.makeKeyAndOrderFront(nil)
        panel.makeFirstResponder(view)
        return true
    }

    private func finish(_ accepted: Bool) {
        guard !finished else { return }
        finished = true
        window?.orderOut(nil)
        window?.close()
        window = nil
        completion(accepted)
    }

    private static func windowRects(mainTop: CGFloat) -> [NSRect] {
        guard let info = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID)
                as? [[String: Any]] else { return [] }
        let ownPID = ProcessInfo.processInfo.processIdentifier
        return info.compactMap { item in
            guard (item[kCGWindowOwnerPID as String] as? NSNumber)?.int32Value != ownPID,
                  (item[kCGWindowLayer as String] as? Int) == 0,
                  let bounds = item[kCGWindowBounds as String] as? NSDictionary,
                  let rect = CGRect(dictionaryRepresentation: bounds as CFDictionary),
                  rect.width > 10, rect.height > 10 else { return nil }
            return NSRect(x: rect.minX, y: mainTop - rect.minY - rect.height, width: rect.width, height: rect.height)
        }
    }
}

private final class SmartScreenshotPanel: NSPanel {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { true }
}

private final class SmartScreenshotView: NSView {
    private let image: NSImage
    private let globalFrame: NSRect
    private let candidates: [NSRect]
    private let completion: (Data?) -> Void
    private var pointer = NSPoint.zero
    private var startPoint = NSPoint.zero
    private var selection = NSRect.zero
    private var dragging = false
    private lazy var bitmap = NSBitmapImageRep(data: image.tiffRepresentation ?? Data())

    override var acceptsFirstResponder: Bool { true }

    init(image: NSImage, globalFrame: NSRect, candidates: [NSRect], completion: @escaping (Data?) -> Void) {
        self.image = image
        self.globalFrame = globalFrame
        self.candidates = candidates
        self.completion = completion
        super.init(frame: NSRect(origin: .zero, size: globalFrame.size))
        let mouse = NSEvent.mouseLocation
        pointer = NSPoint(x: mouse.x - globalFrame.minX, y: mouse.y - globalFrame.minY)
        selection = snap(at: pointer)
    }

    required init?(coder: NSCoder) { nil }

    override func draw(_ dirtyRect: NSRect) {
        image.draw(in: bounds)
        NSColor.black.withAlphaComponent(0.68).setFill()
        bounds.fill()
        if !selection.isEmpty {
            NSGraphicsContext.saveGraphicsState()
            NSBezierPath(rect: selection).addClip()
            image.draw(in: bounds)
            NSGraphicsContext.restoreGraphicsState()
            NSColor.systemPink.setStroke()
            let border = NSBezierPath(rect: selection.insetBy(dx: 1, dy: 1)); border.lineWidth = 2; border.stroke()
        }

        NSColor.white.withAlphaComponent(0.82).setStroke()
        let cross = NSBezierPath(); cross.lineWidth = 1
        cross.move(to: NSPoint(x: 0, y: pointer.y)); cross.line(to: NSPoint(x: bounds.maxX, y: pointer.y))
        cross.move(to: NSPoint(x: pointer.x, y: 0)); cross.line(to: NSPoint(x: pointer.x, y: bounds.maxY)); cross.stroke()
        drawInspector()
    }

    override func mouseMoved(with event: NSEvent) { update(event.locationInWindow) }

    override func mouseDown(with event: NSEvent) {
        startPoint = event.locationInWindow
        dragging = true
    }

    override func mouseDragged(with event: NSEvent) {
        pointer = event.locationInWindow
        selection = normalized(startPoint, pointer)
        needsDisplay = true
    }

    override func mouseUp(with event: NSEvent) {
        let end = event.locationInWindow
        let manual = normalized(startPoint, end)
        dragging = false
        selection = manual.width >= 5 && manual.height >= 5 ? manual : snap(at: end)
        accept()
    }

    override func rightMouseDown(with event: NSEvent) { completion(nil) }

    override func keyDown(with event: NSEvent) {
        if event.keyCode == 53 { completion(nil) }
        else if event.keyCode == 36 || event.keyCode == 76 { accept() }
        else { super.keyDown(with: event) }
    }

    private func update(_ point: NSPoint) {
        pointer = point
        if !dragging { selection = snap(at: point) }
        needsDisplay = true
    }

    private func snap(at local: NSPoint) -> NSRect {
        let global = NSPoint(x: local.x + globalFrame.minX, y: local.y + globalFrame.minY)
        if let screen = NSScreen.screens.first(where: { $0.frame.contains(global) }) {
            let f = screen.frame
            if abs(global.x - f.minX) <= 3 || abs(global.x - f.maxX) <= 3 ||
                abs(global.y - f.minY) <= 3 || abs(global.y - f.maxY) <= 3 {
                return f.offsetBy(dx: -globalFrame.minX, dy: -globalFrame.minY)
            }
        }
        if let rect = candidates.first(where: { $0.contains(global) }) {
            let titleHeight = min(44, max(28, rect.height / 8))
            let target = global.y > rect.maxY - titleHeight
                ? NSRect(x: rect.minX, y: rect.maxY - titleHeight, width: rect.width, height: titleHeight)
                : rect
            return target.offsetBy(dx: -globalFrame.minX, dy: -globalFrame.minY)
        }
        let display = NSScreen.screens.first(where: { $0.frame.contains(global) })?.frame ?? globalFrame
        return display.offsetBy(dx: -globalFrame.minX, dy: -globalFrame.minY)
    }

    private func accept() {
        let r = selection.intersection(bounds)
        guard r.width >= 2, r.height >= 2, let source = image.cgImage(forProposedRect: nil, context: nil, hints: nil) else { return }
        let scaleX = CGFloat(source.width) / bounds.width
        let scaleY = CGFloat(source.height) / bounds.height
        let crop = CGRect(x: r.minX * scaleX, y: (bounds.height - r.maxY) * scaleY,
                          width: r.width * scaleX, height: r.height * scaleY).integral
        guard let cropped = source.cropping(to: crop) else { return }
        let rep = NSBitmapImageRep(cgImage: cropped)
        completion(rep.representation(using: .png, properties: [:]))
    }

    private func drawInspector() {
        let color = pixelColor(at: pointer) ?? .black
        let width: CGFloat = 176, height: CGFloat = 140
        var origin = NSPoint(x: pointer.x + 18, y: pointer.y - height - 18)
        if origin.x + width > bounds.maxX { origin.x = pointer.x - width - 18 }
        if origin.y < 4 { origin.y = pointer.y + 18 }
        let box = NSRect(origin: origin, size: NSSize(width: width, height: height))
        NSColor(calibratedWhite: 0.08, alpha: 0.94).setFill(); NSBezierPath(roundedRect: box, xRadius: 5, yRadius: 5).fill()

        let mag = NSRect(x: box.minX + 8, y: box.minY + 40, width: width - 16, height: 92)
        let source = NSRect(x: pointer.x - 7, y: pointer.y - 5, width: 15, height: 11).intersection(bounds)
        image.draw(in: mag, from: source, operation: .copy, fraction: 1,
                   respectFlipped: false, hints: [.interpolation: NSImageInterpolation.none])
        NSColor.systemPink.withAlphaComponent(0.9).setStroke()
        let reticle = NSBezierPath(); reticle.move(to: NSPoint(x: mag.midX, y: mag.minY)); reticle.line(to: NSPoint(x: mag.midX, y: mag.maxY))
        reticle.move(to: NSPoint(x: mag.minX, y: mag.midY)); reticle.line(to: NSPoint(x: mag.maxX, y: mag.midY)); reticle.stroke()

        let rgb = color.usingColorSpace(.deviceRGB) ?? color
        let globalX = Int(pointer.x + globalFrame.minX)
        let globalY = Int(globalFrame.maxY - (pointer.y + globalFrame.minY))
        let text = "(\(globalX), \(globalY))  RGB(\(Int(rgb.redComponent * 255)), \(Int(rgb.greenComponent * 255)), \(Int(rgb.blueComponent * 255)))"
        (text as NSString).draw(at: NSPoint(x: box.minX + 8, y: box.minY + 19), withAttributes: [
            .font: NSFont.monospacedSystemFont(ofSize: 11, weight: .regular), .foregroundColor: NSColor.white,
        ])
        ("单击磁吸 · 拖动自选 · Esc 退出" as NSString).draw(at: NSPoint(x: box.minX + 8, y: box.minY + 5), withAttributes: [
            .font: NSFont.systemFont(ofSize: 10), .foregroundColor: NSColor.white.withAlphaComponent(0.72),
        ])
    }

    private func pixelColor(at point: NSPoint) -> NSColor? {
        guard let bitmap else { return nil }
        let x = Int(point.x / max(1, bounds.width) * CGFloat(bitmap.pixelsWide))
        let y = Int(point.y / max(1, bounds.height) * CGFloat(bitmap.pixelsHigh))
        return bitmap.colorAt(x: min(max(0, x), bitmap.pixelsWide - 1), y: min(max(0, y), bitmap.pixelsHigh - 1))
    }

    private func normalized(_ a: NSPoint, _ b: NSPoint) -> NSRect {
        NSRect(x: min(a.x, b.x), y: min(a.y, b.y), width: abs(a.x - b.x), height: abs(a.y - b.y)).intersection(bounds)
    }
}
