import Foundation
import Security

/// Stores the random history-encryption key in the login Keychain, sealed to the macOS
/// user account. `kSecAttrAccessibleAfterFirstUnlock` lets the background agent read it
/// after the user has logged in once, without any prompt.
enum Keychain {
    private static let service = "com.cassian.fineclipboard"

    /// Returns the stored key, creating and persisting a fresh random one on first use.
    static func getOrCreateKey(account: String, length: Int = 32) -> Data {
        if let existing = read(account) { return existing }
        var bytes = Data(count: length)
        _ = bytes.withUnsafeMutableBytes { SecRandomCopyBytes(kSecRandomDefault, length, $0.baseAddress!) }
        write(account, bytes)
        return bytes
    }

    /// Stores arbitrary key material for an account (used to seal the derived sync key).
    static func setData(account: String, _ data: Data) { write(account, data) }

    /// Reads previously stored key material, or nil if absent.
    static func getData(account: String) -> Data? { read(account) }

    private static func read(_ account: String) -> Data? {
        let query: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
            kSecReturnData as String: true,
            kSecMatchLimit as String: kSecMatchLimitOne,
        ]
        var result: AnyObject?
        let status = SecItemCopyMatching(query as CFDictionary, &result)
        return status == errSecSuccess ? result as? Data : nil
    }

    private static func write(_ account: String, _ data: Data) {
        // Replace any stale entry, then add.
        let base: [String: Any] = [
            kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service,
            kSecAttrAccount as String: account,
        ]
        SecItemDelete(base as CFDictionary)
        var add = base
        add[kSecValueData as String] = data
        add[kSecAttrAccessible as String] = kSecAttrAccessibleAfterFirstUnlock
        SecItemAdd(add as CFDictionary, nil)
    }
}
