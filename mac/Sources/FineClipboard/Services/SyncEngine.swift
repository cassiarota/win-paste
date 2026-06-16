import Foundation

/// Drives end-to-end-encrypted, incremental sync against finepaste-server. Only text and
/// images sync — passwords are never sent. Each item is encrypted with the passphrase-derived
/// key before upload; the server stores ciphertext only. Mirrors the Windows SyncEngine.
///
/// Invoked from the main thread; store access is hopped onto the main actor around the
/// network calls (the store is main-thread-only).
final class SyncEngine {
    static let defaultBaseURL = "https://cassiangroup.uk/finepaste"
    private static let keychainAccount = "sync-key"
    private static let checkToken = "FINECLIP-SYNC-OK"

    private let store: Store
    private let client = SyncClient()
    private var crypto: SyncCrypto?

    init(_ store: Store) {
        self.store = store
        client.baseURL = (store.setting(Store.syncBaseUrlKey) ?? Self.defaultBaseURL).trimmedSlash()
        client.token = store.setting(Store.syncTokenKey).flatMap { $0.isEmpty ? nil : $0 }
        if let keyData = Keychain.getData(account: Self.keychainAccount) {
            crypto = SyncCrypto(key: keyData)
        }
    }

    var loggedIn: Bool { !(client.token ?? "").isEmpty }
    var hasPassphrase: Bool { crypto != nil }
    var enabled: Bool { store.setting(Store.syncEnabledKey) == "1" }
    var ready: Bool { enabled && loggedIn && crypto != nil }
    var email: String? { store.setting(Store.syncEmailKey) }
    var baseURL: String { client.baseURL }

    func setBaseURL(_ url: String) {
        client.baseURL = url.trimmingCharacters(in: .whitespaces).trimmedSlash()
        store.setSetting(Store.syncBaseUrlKey, client.baseURL)
    }

    func register(email: String, password: String) async throws {
        let r = try await client.register(email: email.trimmingCharacters(in: .whitespaces), password: password)
        saveAuth(email, r)
    }

    func login(email: String, password: String) async throws {
        let r = try await client.login(email: email.trimmingCharacters(in: .whitespaces), password: password)
        saveAuth(email, r)
    }

    private func saveAuth(_ email: String, _ r: SyncClient.AuthResult) {
        client.token = r.token
        store.setSetting(Store.syncEmailKey, email.trimmingCharacters(in: .whitespaces))
        store.setSetting(Store.syncTokenKey, r.token)
    }

    func logout() {
        client.token = nil
        store.setSetting(Store.syncTokenKey, "")
        store.setSetting(Store.syncEnabledKey, "0")
    }

    /// Redeems a VIP activation key; returns true if VIP is now active.
    func redeem(key: String) async throws -> Bool {
        let r = try await client.redeem(key: key.trimmingCharacters(in: .whitespaces))
        return r.vip
    }

    /// Sets the sync passphrase: derives + stores the key (Keychain) and a local verifier.
    func setPassphrase(_ passphrase: String) throws {
        guard let email, !email.isEmpty else {
            throw SyncClient.SyncError(message: "请先登录再设置同步口令")
        }
        let keyData = SyncCrypto.deriveKey(passphrase: passphrase, email: email)
        Keychain.setData(account: Self.keychainAccount, keyData)
        let c = SyncCrypto(key: keyData)
        crypto = c
        store.setSetting(Store.syncCheckKey, c.encryptToBase64(Data(Self.checkToken.utf8)))
    }

    func enable(_ on: Bool) {
        store.setSetting(Store.syncEnabledKey, on ? "1" : "0")
        if on { store.markAllDirty() }
    }

    /// Pushes local changes then pulls remote changes. Returns a status line.
    @discardableResult
    func syncNow() async throws -> String {
        guard ready, let crypto else {
            throw SyncClient.SyncError(message: "同步未就绪(需登录、设置口令并开启同步)")
        }
        let store = self.store

        // ---- build push payload on the main thread ----
        let prep: (items: [SyncClient.PushItem], dirtyIds: [Int64], tombUuids: [String]) =
            await MainActor.run {
                var items: [SyncClient.PushItem] = []
                let dirty = store.dirtyForSync(limit: 200)
                for it in dirty {
                    let uuid = it.syncUuid ?? UUID().uuidString.replacingOccurrences(of: "-", with: "")
                    if it.syncUuid == nil { store.assignSyncUuid(it.id, uuid) }
                    items.append(SyncClient.PushItem(
                        uuid: uuid, kind: it.kind == .image ? 1 : 0,
                        updated_at: it.updatedMs, deleted: false,
                        cipher: crypto.encryptToBase64(Self.buildEnvelope(it))))
                }
                let tombs = store.tombstones()
                for t in tombs {
                    items.append(SyncClient.PushItem(uuid: t.uuid, kind: 0, updated_at: t.updatedMs, deleted: true, cipher: ""))
                }
                return (items, dirty.map { $0.id }, tombs.map { $0.uuid })
            }

        let uploaded = prep.items.count
        if uploaded > 0 {
            _ = try await client.push(retentionDays: await MainActor.run { self.retentionDays() }, items: prep.items)
            await MainActor.run {
                for id in prep.dirtyIds { store.markSynced(id) }
                for u in prep.tombUuids { store.removeTombstone(u) }
            }
        }

        // ---- pull remote changes ----
        var cursor = await MainActor.run { Int64(store.setting(Store.syncCursorKey) ?? "0") ?? 0 }
        var downloaded = 0
        while true {
            let res = try await client.changes(since: cursor, limit: 200)

            // Resolve each change (decrypt text inline / fetch+decrypt image blobs) off the main thread.
            var resolved: [(SyncClient.Change, Envelope?)] = []
            for ch in res.changes {
                if ch.deleted { resolved.append((ch, nil)); continue }
                let cipherData: Data? = ch.kind == 1
                    ? try await client.blob(uuid: ch.uuid)
                    : ch.cipher.flatMap { Data(base64Encoded: $0) }
                guard let cipherData,
                      let plain = crypto.decryptFromBase64(cipherData.base64EncodedString()),
                      let env = try? JSONDecoder().decode(Envelope.self, from: plain) else { continue }
                resolved.append((ch, env))
            }

            let newCursor = res.cursor
            let toApply = resolved
            await MainActor.run {
                for (ch, env) in toApply {
                    if ch.deleted { store.deleteBySyncUuid(ch.uuid); continue }
                    guard let env else { continue }
                    store.upsertFromSync(
                        kind: ch.kind == 1 ? .image : .text,
                        text: env.text, data: env.img.flatMap { Data(base64Encoded: $0) },
                        preview: env.preview ?? "", html: env.html, rtf: env.rtf, ocr: env.ocr,
                        uuid: ch.uuid, updatedMs: ch.updated_at)
                }
                store.setSetting(Store.syncCursorKey, String(newCursor))
            }
            cursor = newCursor
            downloaded += res.changes.count
            if !res.has_more { break }
        }

        return "已同步 · 上传 \(uploaded) · 下载 \(downloaded)"
    }

    private func retentionDays() -> Int {
        let days = Int(store.setting(Store.expiryDaysKey) ?? "0") ?? 0
        return days <= 0 ? 183 : days // server clamps to ~6 months anyway
    }

    // Envelope JSON keys must match the Windows client exactly.
    private struct Envelope: Codable {
        var text: String?
        var html: String?
        var rtf: String?
        var preview: String?
        var ocr: String?
        var img: String?
    }

    private static func buildEnvelope(_ it: ClipItem) -> Data {
        let env = Envelope(text: it.text, html: it.html, rtf: it.rtf,
                           preview: it.preview, ocr: it.ocrText,
                           img: it.data.map { $0.base64EncodedString() })
        return (try? JSONEncoder().encode(env)) ?? Data("{}".utf8)
    }
}

private extension String {
    func trimmedSlash() -> String {
        var s = self
        while s.hasSuffix("/") { s.removeLast() }
        return s
    }
}
