using System;
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace FineClipboard.Services;

/// <summary>
/// End-to-end encryption for synced clipboard content. The key is derived from the user's
/// sync passphrase with Argon2id, using a deterministic salt from the account email so every
/// device with the same passphrase derives the same key. The key never leaves the device —
/// the server only ever stores AES-256-GCM ciphertext (nonce||tag||ciphertext).
/// </summary>
public sealed class SyncCrypto
{
    private const int NonceLen = 12;
    private const int TagLen = 16;

    private readonly byte[] _key;

    private SyncCrypto(byte[] key) => _key = key;

    public static SyncCrypto FromKey(byte[] key) => new(key);

    /// <summary>Derives the 256-bit sync key from passphrase + email (deterministic across devices).</summary>
    public static byte[] DeriveKey(string passphrase, string email)
    {
        byte[] salt = SHA256.HashData(
            Encoding.UTF8.GetBytes("fineclipboard-sync-v1|" + email.Trim().ToLowerInvariant()));
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(passphrase))
        {
            Salt = salt,
            DegreeOfParallelism = 2,
            MemorySize = 65536,
            Iterations = 3,
        };
        return argon.GetBytes(32);
    }

    public string EncryptToBase64(byte[] plain) => Convert.ToBase64String(Encrypt(plain));

    public byte[]? DecryptFromBase64(string? b64)
    {
        if (string.IsNullOrEmpty(b64))
        {
            return null;
        }
        try
        {
            return Decrypt(Convert.FromBase64String(b64));
        }
        catch
        {
            return null; // wrong passphrase / corrupt data
        }
    }

    private byte[] Encrypt(byte[] plain)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceLen);
        byte[] cipher = new byte[plain.Length];
        byte[] tag = new byte[TagLen];
        using var aes = new AesGcm(_key, TagLen);
        aes.Encrypt(nonce, plain, cipher, tag);

        byte[] blob = new byte[NonceLen + TagLen + cipher.Length];
        Buffer.BlockCopy(nonce, 0, blob, 0, NonceLen);
        Buffer.BlockCopy(tag, 0, blob, NonceLen, TagLen);
        Buffer.BlockCopy(cipher, 0, blob, NonceLen + TagLen, cipher.Length);
        return blob;
    }

    private byte[] Decrypt(byte[] blob)
    {
        byte[] nonce = new byte[NonceLen];
        byte[] tag = new byte[TagLen];
        Buffer.BlockCopy(blob, 0, nonce, 0, NonceLen);
        Buffer.BlockCopy(blob, NonceLen, tag, 0, TagLen);
        byte[] cipher = new byte[blob.Length - NonceLen - TagLen];
        Buffer.BlockCopy(blob, NonceLen + TagLen, cipher, 0, cipher.Length);

        byte[] plain = new byte[cipher.Length];
        using var aes = new AesGcm(_key, TagLen);
        aes.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }
}
