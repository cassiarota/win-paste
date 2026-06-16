import Foundation

/// Thin async HTTP client for the finepaste-server sync API.
final class SyncClient {
    var baseURL: String = ""
    var token: String?

    struct SyncError: LocalizedError {
        let message: String
        var errorDescription: String? { message }
    }

    struct AuthResult: Codable {
        let token: String
        let vip: Bool
        let vip_until: Int64?
    }
    struct PushItem: Codable {
        let uuid: String
        let kind: Int
        let updated_at: Int64
        let deleted: Bool
        let cipher: String
    }
    struct PushResult: Codable { let cursor: Int64 }
    struct Change: Codable {
        let uuid: String
        let kind: Int
        let seq: Int64
        let updated_at: Int64
        let deleted: Bool
        let blob_size: Int
        let cipher: String?
    }
    struct ChangesResult: Codable {
        let cursor: Int64
        let has_more: Bool
        let changes: [Change]
    }

    func register(email: String, password: String) async throws -> AuthResult {
        try await postJSON("/api/register", body: ["email": email, "password": password])
    }
    func login(email: String, password: String) async throws -> AuthResult {
        try await postJSON("/api/login", body: ["email": email, "password": password])
    }
    func redeem(key: String) async throws -> AuthResult {
        try await postJSON("/api/redeem", body: ["key": key], authed: true)
    }

    func push(retentionDays: Int, items: [PushItem]) async throws -> PushResult {
        var req = try request("POST", "/api/sync/push", authed: true)
        let body = PushBody(retention_days: retentionDays, items: items)
        req.httpBody = try JSONEncoder().encode(body)
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        return try await send(req)
    }

    func changes(since: Int64, limit: Int = 200) async throws -> ChangesResult {
        let req = try request("GET", "/api/sync/changes?since=\(since)&limit=\(limit)", authed: true)
        return try await send(req)
    }

    func blob(uuid: String) async throws -> Data? {
        let req = try request("GET", "/api/sync/blob/\(uuid)", authed: true)
        let (data, resp) = try await URLSession.shared.data(for: req)
        guard let http = resp as? HTTPURLResponse else { return nil }
        if http.statusCode == 404 { return nil }
        guard (200..<300).contains(http.statusCode) else { throw SyncError(message: "HTTP \(http.statusCode)") }
        return data
    }

    // MARK: - helpers

    private struct PushBody: Codable {
        let retention_days: Int
        let items: [PushItem]
    }

    private func request(_ method: String, _ path: String, authed: Bool = false) throws -> URLRequest {
        guard let url = URL(string: baseURL + path) else { throw SyncError(message: "无效的服务器地址") }
        var req = URLRequest(url: url)
        req.httpMethod = method
        if authed, let token { req.setValue("Bearer \(token)", forHTTPHeaderField: "Authorization") }
        return req
    }

    private func postJSON<T: Decodable>(_ path: String, body: [String: String], authed: Bool = false) async throws -> T {
        var req = try request("POST", path, authed: authed)
        req.httpBody = try JSONEncoder().encode(body)
        req.setValue("application/json", forHTTPHeaderField: "Content-Type")
        return try await send(req)
    }

    private func send<T: Decodable>(_ req: URLRequest) async throws -> T {
        let (data, resp) = try await URLSession.shared.data(for: req)
        guard let http = resp as? HTTPURLResponse else { throw SyncError(message: "无响应") }
        guard (200..<300).contains(http.statusCode) else {
            throw SyncError(message: errorMessage(data) ?? "HTTP \(http.statusCode)")
        }
        return try JSONDecoder().decode(T.self, from: data)
    }

    private func errorMessage(_ data: Data) -> String? {
        struct E: Codable { let error: String? }
        return (try? JSONDecoder().decode(E.self, from: data))?.error
    }
}
