import SwiftUI
import AppKit

struct ScreenshotPreviewView: View {
    let data: Data

    private var image: NSImage? { NSImage(data: data) }

    var body: some View {
        VStack(spacing: 14) {
            HStack(alignment: .top) {
                VStack(alignment: .leading, spacing: 10) {
                    Text("截图预览")
                        .font(.title3.weight(.semibold))
                    HStack(spacing: 8) {
                        Text("默认形状")
                            .foregroundStyle(.secondary)
                        Button("矩形") {}
                        Button("箭头") {}
                        Button("画笔") {}
                        Button("马赛克") {}
                    }
                    .font(.callout)
                }
                Spacer()
                Button("另存为...") { save() }
            }

            ZStack {
                RoundedRectangle(cornerRadius: 16)
                    .fill(.black.opacity(0.18))
                    .overlay(RoundedRectangle(cornerRadius: 16).stroke(.white.opacity(0.20)))
                if let image {
                    Image(nsImage: image)
                        .resizable()
                        .aspectRatio(contentMode: .fit)
                        .padding(12)
                } else {
                    Text("无法显示截图")
                        .foregroundStyle(.secondary)
                }
            }
        }
        .padding(18)
        .background(.regularMaterial)
        .frame(minWidth: 680, minHeight: 500)
    }

    private func save() {
        let panel = NSSavePanel()
        panel.nameFieldStringValue = "screenshot.png"
        if #available(macOS 11.0, *) { panel.allowedContentTypes = [.png] }
        if panel.runModal() == .OK, let url = panel.url {
            try? data.write(to: url)
        }
    }
}
