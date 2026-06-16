import Foundation
import Vision
import AppKit

/// On-device text recognition for captured images, using the built-in Vision framework
/// (the same engine behind Live Text). No network, no third-party dependency. Recognized
/// text is stored alongside the image so screenshots become searchable.
enum OCR {
    /// Recognizes text in PNG image data. `completion` is called off the main thread with the
    /// recognized text, or nil when nothing is found / recognition fails.
    static func recognize(_ png: Data, completion: @escaping (String?) -> Void) {
        guard let cg = NSBitmapImageRep(data: png)?.cgImage else { completion(nil); return }

        let request = VNRecognizeTextRequest { req, _ in
            let text = (req.results as? [VNRecognizedTextObservation] ?? [])
                .compactMap { $0.topCandidates(1).first?.string }
                .joined(separator: "\n")
                .trimmingCharacters(in: .whitespacesAndNewlines)
            completion(text.isEmpty ? nil : text)
        }
        request.recognitionLevel = .accurate
        request.usesLanguageCorrection = true
        // macOS 13+ ships Simplified/Traditional Chinese + English recognition.
        request.recognitionLanguages = ["zh-Hans", "zh-Hant", "en-US"]

        let handler = VNImageRequestHandler(cgImage: cg, options: [:])
        DispatchQueue.global(qos: .utility).async {
            do { try handler.perform([request]) } catch { completion(nil) }
        }
    }
}
