import SwiftUI
import AppKit

private enum ScreenshotTool: String, CaseIterable, Hashable {
    case select = "选择", rect = "画框", arrow = "箭头", pen = "画笔", mosaic = "马赛克", text = "文字"
}

private struct ScreenshotMark {
    enum Kind { case rect, arrow, pen, mosaic, text }
    var kind: Kind
    var points: [NSPoint]
    var text: String = ""
}

private final class ScreenshotEditorModel: ObservableObject {
    let image: NSImage
    @Published var tool: ScreenshotTool = .select
    @Published var marks: [ScreenshotMark] = []
    @Published var status = "选择工具后在图片上拖动；文字工具单击后输入。"

    init(data: Data) { image = NSImage(data: data) ?? NSImage(size: NSSize(width: 1, height: 1)) }

    func begin(at point: NSPoint) {
        switch tool {
        case .select: return
        case .text:
            guard let value = Prompt.text("添加文字", "标注内容"), !value.isEmpty else { return }
            marks.append(ScreenshotMark(kind: .text, points: [point], text: value))
        case .rect: marks.append(ScreenshotMark(kind: .rect, points: [point, point]))
        case .arrow: marks.append(ScreenshotMark(kind: .arrow, points: [point, point]))
        case .pen: marks.append(ScreenshotMark(kind: .pen, points: [point]))
        case .mosaic: marks.append(ScreenshotMark(kind: .mosaic, points: [point]))
        }
    }

    func drag(to point: NSPoint) {
        guard !marks.isEmpty else { return }
        switch marks[marks.count - 1].kind {
        case .rect, .arrow:
            if marks[marks.count - 1].points.count == 1 { marks[marks.count - 1].points.append(point) }
            else { marks[marks.count - 1].points[1] = point }
        case .pen, .mosaic: marks[marks.count - 1].points.append(point)
        case .text: break
        }
    }

    func undo() { if !marks.isEmpty { marks.removeLast() } }

    func renderedPNG() -> Data? {
        let size = image.size
        let result = NSImage(size: size)
        result.lockFocus()
        image.draw(in: NSRect(origin: .zero, size: size))
        ScreenshotEditorCanvas.draw(marks: marks, image: image)
        result.unlockFocus()
        guard let tiff = result.tiffRepresentation, let rep = NSBitmapImageRep(data: tiff) else { return nil }
        return rep.representation(using: .png, properties: [:])
    }

    func copy() {
        guard let data = renderedPNG() else { status = "复制失败"; return }
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setData(data, forType: .png)
        status = "已复制标注后的截图"
    }

    func save() {
        guard let data = renderedPNG() else { status = "导出失败"; return }
        let panel = NSSavePanel(); panel.nameFieldStringValue = "screenshot.png"
        if #available(macOS 11.0, *) { panel.allowedContentTypes = [.png] }
        if panel.runModal() == .OK, let url = panel.url {
            do { try data.write(to: url); status = "已导出到 \(url.path)" } catch { status = "导出失败：\(error.localizedDescription)" }
        }
    }
}

struct ScreenshotPreviewView: View {
    @StateObject private var model: ScreenshotEditorModel

    init(data: Data) { _model = StateObject(wrappedValue: ScreenshotEditorModel(data: data)) }

    var body: some View {
        VStack(spacing: 12) {
            HStack(spacing: 6) {
                Text("截图编辑").font(.title3.weight(.semibold)).padding(.trailing, 8)
                ForEach(ScreenshotTool.allCases, id: \.self) { tool in
                    Button(tool.rawValue) { model.tool = tool }
                        .buttonStyle(.borderedProminent)
                        .tint(model.tool == tool ? .accentColor : .gray.opacity(0.45))
                }
                Button("撤销") { model.undo() }.disabled(model.marks.isEmpty)
                Spacer()
                Button("复制") { model.copy() }
                Button("导出…") { model.save() }
            }

            ScreenshotEditorRepresentable(model: model)
                .background(Color.black.opacity(0.65))
                .overlay(Rectangle().stroke(Color.white.opacity(0.18)))

            Text(model.status).font(.caption).foregroundStyle(.secondary).frame(maxWidth: .infinity, alignment: .leading)
        }
        .padding(14)
        .background(.regularMaterial)
        .frame(minWidth: 760, minHeight: 560)
    }
}

private struct ScreenshotEditorRepresentable: NSViewRepresentable {
    @ObservedObject var model: ScreenshotEditorModel
    func makeNSView(context: Context) -> ScreenshotEditorCanvas { ScreenshotEditorCanvas(model: model) }
    func updateNSView(_ view: ScreenshotEditorCanvas, context: Context) { view.model = model; view.needsDisplay = true }
}

private final class ScreenshotEditorCanvas: NSView {
    var model: ScreenshotEditorModel
    private var drawing = false
    override var acceptsFirstResponder: Bool { true }

    init(model: ScreenshotEditorModel) { self.model = model; super.init(frame: .zero) }
    required init?(coder: NSCoder) { nil }

    override func draw(_ dirtyRect: NSRect) {
        NSColor.black.withAlphaComponent(0.55).setFill(); bounds.fill()
        let target = imageRect
        model.image.draw(in: target)
        guard let context = NSGraphicsContext.current?.cgContext else { return }
        context.saveGState(); context.translateBy(x: target.minX, y: target.minY)
        let scale = target.width / max(1, model.image.size.width); context.scaleBy(x: scale, y: scale)
        Self.draw(marks: model.marks, image: model.image)
        context.restoreGState()
    }

    override func mouseDown(with event: NSEvent) {
        guard let p = imagePoint(event.locationInWindow) else { return }
        drawing = model.tool != .select && model.tool != .text
        model.begin(at: p); needsDisplay = true
    }

    override func mouseDragged(with event: NSEvent) {
        guard drawing, let p = imagePoint(event.locationInWindow) else { return }
        model.drag(to: p); needsDisplay = true
    }

    override func mouseUp(with event: NSEvent) { drawing = false }

    private var imageRect: NSRect {
        let size = model.image.size
        let scale = min(bounds.width / max(1, size.width), bounds.height / max(1, size.height))
        let fitted = NSSize(width: size.width * scale, height: size.height * scale)
        return NSRect(x: (bounds.width - fitted.width) / 2, y: (bounds.height - fitted.height) / 2,
                      width: fitted.width, height: fitted.height)
    }

    private func imagePoint(_ point: NSPoint) -> NSPoint? {
        let rect = imageRect
        guard rect.contains(point) else { return nil }
        let scale = model.image.size.width / max(1, rect.width)
        return NSPoint(x: (point.x - rect.minX) * scale, y: (point.y - rect.minY) * scale)
    }

    fileprivate static func draw(marks: [ScreenshotMark], image: NSImage) {
        NSColor.systemRed.setStroke(); NSColor.systemRed.setFill()
        for mark in marks {
            switch mark.kind {
            case .rect:
                NSColor.systemRed.setStroke()
                guard mark.points.count >= 2 else { continue }
                let r = normalized(mark.points[0], mark.points[1]); let path = NSBezierPath(rect: r); path.lineWidth = 3; path.stroke()
            case .arrow:
                NSColor.systemRed.setStroke(); NSColor.systemRed.setFill()
                guard mark.points.count >= 2 else { continue }; drawArrow(from: mark.points[0], to: mark.points[1])
            case .pen:
                NSColor.systemRed.setStroke()
                guard let first = mark.points.first else { continue }
                let path = NSBezierPath(); path.lineWidth = 4; path.lineCapStyle = .round; path.lineJoinStyle = .round; path.move(to: first)
                for p in mark.points.dropFirst() { path.line(to: p) }; path.stroke()
            case .mosaic:
                drawMosaic(points: mark.points, image: image)
            case .text:
                guard let point = mark.points.first else { continue }
                (mark.text as NSString).draw(at: point, withAttributes: [
                    .font: NSFont.boldSystemFont(ofSize: max(18, image.size.width / 60)), .foregroundColor: NSColor.systemRed,
                    .strokeColor: NSColor.white, .strokeWidth: -1.5,
                ])
            }
        }
    }

    private static func drawArrow(from a: NSPoint, to b: NSPoint) {
        let path = NSBezierPath(); path.lineWidth = 4; path.move(to: a); path.line(to: b); path.stroke()
        let angle = atan2(b.y - a.y, b.x - a.x), length: CGFloat = 20, spread: CGFloat = 0.55
        let head = NSBezierPath(); head.move(to: b)
        head.line(to: NSPoint(x: b.x - length * cos(angle - spread), y: b.y - length * sin(angle - spread)))
        head.line(to: NSPoint(x: b.x - length * cos(angle + spread), y: b.y - length * sin(angle + spread)))
        head.close(); head.fill()
    }

    private static func drawMosaic(points: [NSPoint], image: NSImage) {
        guard let tiff = image.tiffRepresentation, let bitmap = NSBitmapImageRep(data: tiff) else { return }
        let block: CGFloat = 18
        var drawn = Set<String>()
        for p in points {
            let x = floor(p.x / block) * block, y = floor(p.y / block) * block, key = "\(Int(x))-\(Int(y))"
            guard drawn.insert(key).inserted else { continue }
            let scaleX = CGFloat(bitmap.pixelsWide) / max(1, image.size.width)
            let scaleY = CGFloat(bitmap.pixelsHigh) / max(1, image.size.height)
            let px = min(max(0, Int((x + block / 2) * scaleX)), bitmap.pixelsWide - 1)
            let py = min(max(0, Int((y + block / 2) * scaleY)), bitmap.pixelsHigh - 1)
            (bitmap.colorAt(x: px, y: py) ?? .gray).setFill()
            NSRect(x: x, y: y, width: min(block, image.size.width - x), height: min(block, image.size.height - y)).fill()
        }
    }

    private static func normalized(_ a: NSPoint, _ b: NSPoint) -> NSRect {
        NSRect(x: min(a.x, b.x), y: min(a.y, b.y), width: abs(a.x - b.x), height: abs(a.y - b.y))
    }
}
