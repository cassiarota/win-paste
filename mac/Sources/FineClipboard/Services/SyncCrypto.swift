import Foundation
import CryptoKit
import CArgon2

/// End-to-end encryption for synced clipboard content. The key is derived from the user's
/// sync passphrase with Argon2id and a deterministic salt from the account email, so every
/// device with the same passphrase derives the same key. Byte-for-byte compatible with the
/// Windows client: same Argon2id params (t=3, m=64MiB, p=2) and the same blob layout
/// (nonce(12) || tag(16) || ciphertext) so Mac and Windows decrypt each other's data.
struct SyncCrypto {
    private let key: SymmetricKey

    init(key: Data) { self.key = SymmetricKey(data: key) }

    /// Derives the 256-bit sync key (deterministic across devices).
    static func deriveKey(passphrase: String, email: String) -> Data {
        let salt = Data(SHA256.hash(data: Data("fineclipboard-sync-v1|\(email.trimmingCharacters(in: .whitespaces).lowercased())".utf8)))
        var out = [UInt8](repeating: 0, count: 32)
        let pw = [UInt8](passphrase.utf8)
        let saltBytes = [UInt8](salt)
        let rc = argon2id_hash_raw(3, 65_536, 2, pw, pw.count, saltBytes, saltBytes.count, &out, 32)
        precondition(rc == 0, "argon2id_hash_raw failed: \(rc)")
        return Data(out)
    }

    func encryptToBase64(_ plain: Data) -> String { encrypt(plain).base64EncodedString() }

    func decryptFromBase64(_ b64: String?) -> Data? {
        guard let b64, let blob = Data(base64Encoded: b64) else { return nil }
        return decrypt(blob)
    }

    /// nonce(12) || tag(16) || ciphertext — matches the Windows SyncCrypto layout.
    private func encrypt(_ plain: Data) -> Data {
        let sealed = try! AES.GCM.seal(plain, using: key)
        return Data(sealed.nonce) + sealed.tag + sealed.ciphertext
    }

    private func decrypt(_ blob: Data) -> Data? {
        guard blob.count >= 28 else { return nil }
        let nonceData = blob.prefix(12)
        let tag = blob.subdata(in: 12..<28)
        let ct = blob.subdata(in: 28..<blob.count)
        guard let nonce = try? AES.GCM.Nonce(data: nonceData),
              let box = try? AES.GCM.SealedBox(nonce: nonce, ciphertext: ct, tag: tag) else { return nil }
        return try? AES.GCM.open(box, using: key)
    }
}
