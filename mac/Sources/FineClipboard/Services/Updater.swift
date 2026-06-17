import Foundation

/// Checks the GitHub Releases API for a newer version. Mirrors the Windows `UpdateService`.
enum Updater {
    static let currentVersion = AppInfo.version
    private static let url = URL(string: "https://api.github.com/repos/cassiarota/fine-clipboard/releases/latest")!
    private static let vpsVersion = URL(string: "https://cassiangroup.uk/fineclipboard/download/version.txt")!
    private static let vpsDownload = "https://cassiangroup.uk/fineclipboard/download/FineClipboard-mac.zip"

    struct Update { let version: String; let url: String }

    /// Outcome of a check, distinguishing a real failure from "already current" so callers
    /// never report "up to date" when the request actually failed.
    enum CheckResult { case upToDate, available(Update), failed }

    static func check(completion: @escaping (CheckResult) -> Void) {
        var req = URLRequest(url: url)
        req.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        req.setValue("FineClipboard", forHTTPHeaderField: "User-Agent")
        URLSession.shared.dataTask(with: req) { data, resp, err in
            guard err == nil, let data,
                  let http = resp as? HTTPURLResponse, (200..<300).contains(http.statusCode),
                  let json = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
                  let tag = json["tag_name"] as? String else {
                checkVps(completion: completion)
                return
            }
            let latest = tag.hasPrefix("v") ? String(tag.dropFirst()) : tag
            let html = (json["html_url"] as? String) ?? "https://github.com/cassiarota/fine-clipboard/releases/latest"
            let result: CheckResult = isNewer(latest, than: currentVersion)
                ? .available(Update(version: latest, url: html)) : .upToDate
            DispatchQueue.main.async { completion(result) }
        }.resume()
    }

    private static func checkVps(completion: @escaping (CheckResult) -> Void) {
        var req = URLRequest(url: vpsVersion)
        req.setValue("FineClipboard", forHTTPHeaderField: "User-Agent")
        URLSession.shared.dataTask(with: req) { data, resp, err in
            guard err == nil, let data,
                  let http = resp as? HTTPURLResponse, (200..<300).contains(http.statusCode),
                  let version = String(data: data, encoding: .utf8)?.trimmingCharacters(in: .whitespacesAndNewlines),
                  !version.isEmpty else {
                DispatchQueue.main.async { completion(.failed) }
                return
            }
            let result: CheckResult = isNewer(version, than: currentVersion)
                ? .available(Update(version: version, url: vpsDownload)) : .upToDate
            DispatchQueue.main.async { completion(result) }
        }.resume()
    }

    static func isNewer(_ a: String, than b: String) -> Bool {
        let pa = a.split(separator: ".").map { Int($0) ?? 0 }
        let pb = b.split(separator: ".").map { Int($0) ?? 0 }
        for i in 0..<max(pa.count, pb.count) {
            let x = i < pa.count ? pa[i] : 0
            let y = i < pb.count ? pb[i] : 0
            if x != y { return x > y }
        }
        return false
    }
}
