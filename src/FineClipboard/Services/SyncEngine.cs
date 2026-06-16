using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using FineClipboard.Models;

namespace FineClipboard.Services;

/// <summary>
/// Drives end-to-end-encrypted, incremental sync against finepaste-server. Only text and
/// images sync — passwords are never sent (the vault is excluded entirely). Each item is
/// encrypted with the passphrase-derived key before upload; the server stores ciphertext only.
/// </summary>
public sealed class SyncEngine
{
    public const string DefaultBaseUrl = "https://cassiangroup.uk/finepaste";
    private const string CheckToken = "FINECLIP-SYNC-OK";

    private readonly HistoryStore _store;
    private readonly SyncClient _client = new();
    private SyncCrypto? _crypto;

    public SyncEngine(HistoryStore store)
    {
        _store = store;
        _client.BaseUrl = (_store.GetSetting(HistoryStore.SyncBaseUrlKey) ?? DefaultBaseUrl).TrimEnd('/');
        _client.Token = _store.GetSetting(HistoryStore.SyncTokenKey);
        byte[]? key = UnsealKey();
        if (key != null)
        {
            _crypto = SyncCrypto.FromKey(key);
        }
    }

    public bool LoggedIn => !string.IsNullOrEmpty(_client.Token);
    public bool HasPassphrase => _crypto != null;
    public bool Enabled => _store.GetSetting(HistoryStore.SyncEnabledKey) == "1";
    public bool Ready => Enabled && LoggedIn && _crypto != null;
    public string? Email => _store.GetSetting(HistoryStore.SyncEmailKey);
    public string BaseUrl => _client.BaseUrl;

    public void SetBaseUrl(string url)
    {
        _client.BaseUrl = url.Trim().TrimEnd('/');
        _store.SetSetting(HistoryStore.SyncBaseUrlKey, _client.BaseUrl);
    }

    public async Task RegisterAsync(string email, string password)
    {
        SyncClient.AuthResult? r = await _client.RegisterAsync(email.Trim(), password);
        SaveAuth(email, r);
    }

    public async Task LoginAsync(string email, string password)
    {
        SyncClient.AuthResult? r = await _client.LoginAsync(email.Trim(), password);
        SaveAuth(email, r);
    }

    private void SaveAuth(string email, SyncClient.AuthResult? r)
    {
        if (r == null || string.IsNullOrEmpty(r.Token))
        {
            throw new SyncException("登录失败");
        }
        _client.Token = r.Token;
        _store.SetSetting(HistoryStore.SyncEmailKey, email.Trim());
        _store.SetSetting(HistoryStore.SyncTokenKey, r.Token);
    }

    public void Logout()
    {
        _client.Token = null;
        _store.SetSetting(HistoryStore.SyncTokenKey, string.Empty);
        _store.SetSetting(HistoryStore.SyncEnabledKey, "0");
    }

    /// <summary>Redeems a VIP activation key; returns true if VIP is now active.</summary>
    public async Task<bool> RedeemAsync(string key)
    {
        SyncClient.AuthResult? r = await _client.RedeemAsync(key.Trim());
        return r?.Vip ?? false;
    }

    /// <summary>Sets the sync passphrase: derives + seals the key and stores a local verifier.</summary>
    public void SetPassphrase(string passphrase)
    {
        if (string.IsNullOrEmpty(Email))
        {
            throw new SyncException("请先登录再设置同步口令");
        }
        byte[] key = SyncCrypto.DeriveKey(passphrase, Email);
        SealKey(key);
        _crypto = SyncCrypto.FromKey(key);
        _store.SetSetting(HistoryStore.SyncCheckKey,
            _crypto.EncryptToBase64(Encoding.UTF8.GetBytes(CheckToken)));
    }

    public void Enable(bool on)
    {
        _store.SetSetting(HistoryStore.SyncEnabledKey, on ? "1" : "0");
        if (on)
        {
            _store.MarkAllDirty(); // first enable: push existing text/image history
        }
    }

    /// <summary>Pushes local changes then pulls remote changes. Returns a status line.</summary>
    public async Task<string> SyncNowAsync()
    {
        if (!Ready)
        {
            throw new SyncException("同步未就绪(需登录、设置口令并开启同步)");
        }

        // ---- push dirty items + tombstones ----
        List<ClipboardItem> dirty = _store.GetDirtyForSync(200);
        var items = new List<SyncClient.PushItem>();
        foreach (ClipboardItem it in dirty)
        {
            string uuid = it.SyncUuid ?? Guid.NewGuid().ToString("N");
            if (it.SyncUuid == null)
            {
                _store.AssignSyncUuid(it.Id, uuid);
            }
            byte[] envelope = BuildEnvelope(it);
            items.Add(new SyncClient.PushItem
            {
                Uuid = uuid,
                Kind = it.Type == ClipItemType.Image ? 1 : 0,
                UpdatedAt = it.UpdatedMs,
                Deleted = false,
                Cipher = _crypto!.EncryptToBase64(envelope),
            });
        }
        List<(string Uuid, long UpdatedMs)> tombs = _store.GetTombstones();
        foreach ((string uuid, long ms) in tombs)
        {
            items.Add(new SyncClient.PushItem { Uuid = uuid, Kind = 0, UpdatedAt = ms, Deleted = true, Cipher = string.Empty });
        }

        int uploaded = items.Count;
        if (uploaded > 0)
        {
            await _client.PushAsync(RetentionDays(), items);
            foreach (ClipboardItem it in dirty)
            {
                _store.MarkSynced(it.Id);
            }
            foreach ((string uuid, long _) in tombs)
            {
                _store.RemoveTombstone(uuid);
            }
        }

        // ---- pull remote changes ----
        long cursor = long.TryParse(_store.GetSetting(HistoryStore.SyncCursorKey), out long c) ? c : 0;
        int downloaded = 0;
        while (true)
        {
            SyncClient.ChangesResult res = await _client.ChangesAsync(cursor, 200);
            foreach (SyncClient.Change ch in res.Changes)
            {
                await ApplyChangeAsync(ch);
            }
            cursor = res.Cursor;
            _store.SetSetting(HistoryStore.SyncCursorKey, cursor.ToString(System.Globalization.CultureInfo.InvariantCulture));
            downloaded += res.Changes.Count;
            if (!res.HasMore)
            {
                break;
            }
        }

        return $"已同步 · 上传 {uploaded} · 下载 {downloaded}";
    }

    private async Task ApplyChangeAsync(SyncClient.Change ch)
    {
        if (ch.Deleted)
        {
            _store.DeleteBySyncUuid(ch.Uuid);
            return;
        }

        byte[]? cipher;
        if (ch.Kind == 1)
        {
            cipher = await _client.BlobAsync(ch.Uuid); // images stored as blobs, fetched lazily
        }
        else
        {
            cipher = string.IsNullOrEmpty(ch.Cipher) ? null : Convert.FromBase64String(ch.Cipher);
        }
        if (cipher == null)
        {
            return;
        }

        byte[]? plain = _crypto!.DecryptFromBase64(Convert.ToBase64String(cipher));
        if (plain == null)
        {
            return; // wrong passphrase / corrupt — skip
        }
        ClipboardItem? item = ParseEnvelope(plain, ch.Kind);
        if (item != null)
        {
            _store.UpsertFromSync(item, ch.Uuid, ch.UpdatedAt);
        }
    }

    private int RetentionDays()
    {
        int days = int.TryParse(_store.GetSetting(HistoryStore.ExpiryDaysKey), out int d) ? d : 0;
        return days <= 0 ? 183 : days; // server clamps to ~6 months anyway
    }

    private sealed class Envelope
    {
        [JsonPropertyName("text")] public string? Text { get; set; }
        [JsonPropertyName("html")] public string? Html { get; set; }
        [JsonPropertyName("rtf")] public string? Rtf { get; set; }
        [JsonPropertyName("preview")] public string? Preview { get; set; }
        [JsonPropertyName("ocr")] public string? Ocr { get; set; }
        [JsonPropertyName("img")] public string? Img { get; set; }
    }

    private static byte[] BuildEnvelope(ClipboardItem it)
    {
        var env = new Envelope
        {
            Text = it.Text,
            Html = it.Html,
            Rtf = it.Rtf,
            Preview = it.Preview,
            Ocr = it.OcrText,
            Img = it.ImageData != null ? Convert.ToBase64String(it.ImageData) : null,
        };
        return JsonSerializer.SerializeToUtf8Bytes(env);
    }

    private static ClipboardItem? ParseEnvelope(byte[] plain, int kind)
    {
        Envelope? env;
        try
        {
            env = JsonSerializer.Deserialize<Envelope>(plain);
        }
        catch
        {
            return null;
        }
        if (env == null)
        {
            return null;
        }
        return new ClipboardItem
        {
            Type = kind == 1 ? ClipItemType.Image : ClipItemType.Text,
            Text = env.Text,
            Html = env.Html,
            Rtf = env.Rtf,
            Preview = env.Preview ?? string.Empty,
            OcrText = env.Ocr,
            ImageData = env.Img != null ? Convert.FromBase64String(env.Img) : null,
        };
    }

    private void SealKey(byte[] key)
    {
        byte[] sealedKey = ProtectedData.Protect(key, null, DataProtectionScope.CurrentUser);
        _store.SetSetting(HistoryStore.SyncKeyKey, Convert.ToBase64String(sealedKey));
    }

    private byte[]? UnsealKey()
    {
        string? s = _store.GetSetting(HistoryStore.SyncKeyKey);
        if (string.IsNullOrEmpty(s))
        {
            return null;
        }
        try
        {
            return ProtectedData.Unprotect(Convert.FromBase64String(s), null, DataProtectionScope.CurrentUser);
        }
        catch
        {
            return null;
        }
    }
}
