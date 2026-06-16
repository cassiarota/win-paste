using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using FineClipboard.Models;

namespace FineClipboard.Services;

/// <summary>
/// SQLite-backed clipboard history. Persists across reboots (the key gap in the
/// built-in Windows clipboard). Pinned items always sort first and are never trimmed.
/// </summary>
public sealed class HistoryStore : IDisposable
{
    private const int MaxUnpinnedItems = 1000;

    /// <summary>Settings key: number of days before unpinned items expire (0 = never).</summary>
    public const string ExpiryDaysKey = "expiry_days";

    /// <summary>Settings key: newline-separated process names whose copies are not recorded.</summary>
    public const string ExclusionsKey = "exclusions";

    /// <summary>Settings key: history-popup hotkey, serialized as "modifiers:vk".</summary>
    public const string HotkeyPopupKey = "hotkey_popup";

    /// <summary>Settings key: paste-recent-as-plain-text hotkey, serialized as "modifiers:vk".</summary>
    public const string HotkeyPlainKey = "hotkey_plain";

    /// <summary>Settings key: paste-next-from-stack hotkey, serialized as "modifiers:vk".</summary>
    public const string HotkeyStackKey = "hotkey_stack";

    /// <summary>Settings key: screenshot (region capture) hotkey, serialized as "modifiers:vk".</summary>
    public const string HotkeyShotKey = "hotkey_shot";

    /// <summary>Settings key: "1" to play a sound when a new clipboard item is captured.</summary>
    public const string SoundEnabledKey = "sound_enabled";

    /// <summary>Settings key: max number of unpinned items to keep (default 1000).</summary>
    public const string MaxItemsKey = "max_items";

    /// <summary>Settings key: appearance theme — "system" / "light" / "dark".</summary>
    public const string ThemeKey = "theme";

    /// <summary>Settings key: popup size — "small" / "medium" / "large".</summary>
    public const string PopupSizeKey = "popup_size";

    /// <summary>Settings key: "1" once the first-run welcome has been shown.</summary>
    public const string FirstRunKey = "first_run_done";

    /// <summary>Settings key: base64 random salt for the password vault's KDF.</summary>
    public const string PwSaltKey = "pw_salt";

    /// <summary>Settings key: base64 verifier (a known token encrypted with the master key).</summary>
    public const string PwCheckKey = "pw_check";

    /// <summary>Settings key: KDF used by the password vault — "argon2id" or legacy "pbkdf2".</summary>
    public const string PwKdfKey = "pw_kdf";

    /// <summary>Settings key: DPAPI-sealed random key that encrypts history content.</summary>
    public const string DbKeyKey = "db_key";

    /// <summary>Settings key: "1" once history content has been encrypted.</summary>
    public const string EncVersionKey = "enc_version";

    /// <summary>Sync settings: base URL, account email, bearer token, pull cursor, enabled flag.</summary>
    public const string SyncBaseUrlKey = "sync_base_url";
    public const string SyncEmailKey = "sync_email";
    public const string SyncTokenKey = "sync_token";
    public const string SyncCursorKey = "sync_cursor";
    public const string SyncEnabledKey = "sync_enabled";
    /// <summary>DPAPI-sealed sync key (passphrase-derived) + a verifier token.</summary>
    public const string SyncKeyKey = "sync_key";
    public const string SyncCheckKey = "sync_check";

    private readonly SqliteConnection _conn;
    private readonly ContentCipher _cipher;

    public HistoryStore()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FineClipboard");
        Directory.CreateDirectory(dir);
        string dbPath = Path.Combine(dir, "history.db");

        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        Initialize();

        // The data dir lives under the per-user LocalApplicationData profile (already private
        // to this Windows account); the DPAPI-sealed content key adds at-rest encryption on top.
        _cipher = ContentCipher.Load(this);
        MigrateEncryption();
    }

    private void Initialize()
    {
        using SqliteCommand cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS items (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                type         INTEGER NOT NULL,
                text         TEXT,
                image        BLOB,
                preview      TEXT NOT NULL,
                source_app   TEXT,
                pinned       INTEGER NOT NULL DEFAULT 0,
                content_hash TEXT NOT NULL,
                created_at   TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_items_order ON items(pinned DESC, created_at DESC);
            CREATE INDEX IF NOT EXISTS idx_items_hash ON items(content_hash);
            CREATE TABLE IF NOT EXISTS meta (
                key   TEXT PRIMARY KEY,
                value TEXT
            );
            CREATE TABLE IF NOT EXISTS snippets (
                id    INTEGER PRIMARY KEY AUTOINCREMENT,
                name  TEXT NOT NULL,
                text  TEXT NOT NULL,
                sort  INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS passwords (
                id    INTEGER PRIMARY KEY AUTOINCREMENT,
                name  TEXT NOT NULL,
                blob  BLOB NOT NULL,
                sort  INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS lists (
                id    INTEGER PRIMARY KEY AUTOINCREMENT,
                name  TEXT NOT NULL,
                sort  INTEGER NOT NULL DEFAULT 0
            );
            CREATE TABLE IF NOT EXISTS sync_tombstones (
                sync_uuid  TEXT PRIMARY KEY,
                updated_ms INTEGER NOT NULL
            );";
        cmd.ExecuteNonQuery();

        EnsureItemsColumn("list_id", "INTEGER");
        // Format fidelity (rich text) + OCR text for images. All encrypted at rest.
        EnsureItemsColumn("html", "TEXT");
        EnsureItemsColumn("rtf", "TEXT");
        EnsureItemsColumn("ocr_text", "TEXT");
        // Cross-device sync bookkeeping.
        EnsureItemsColumn("sync_uuid", "TEXT");
        EnsureItemsColumn("updated_ms", "INTEGER NOT NULL DEFAULT 0");
        EnsureItemsColumn("sync_dirty", "INTEGER NOT NULL DEFAULT 1");
        using SqliteCommand idx = _conn.CreateCommand();
        idx.CommandText = "CREATE INDEX IF NOT EXISTS idx_items_syncuuid ON items(sync_uuid)";
        idx.ExecuteNonQuery();
    }

    /// <summary>Adds a column to <c>items</c> if a pre-existing database lacks it (lightweight migration).</summary>
    private void EnsureItemsColumn(string name, string type)
    {
        bool exists = false;
        using (SqliteCommand info = _conn.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(items)";
            using SqliteDataReader reader = info.ExecuteReader();
            while (reader.Read())
            {
                if (reader.GetString(1) == name)
                {
                    exists = true;
                    break;
                }
            }
        }
        if (!exists)
        {
            using SqliteCommand alter = _conn.CreateCommand();
            alter.CommandText = $"ALTER TABLE items ADD COLUMN {name} {type}";
            alter.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Adds an item. If identical content already exists, bumps it to the top
    /// (move-to-front) instead of inserting a duplicate.
    /// </summary>
    /// <summary>Returns the row id of the inserted item, or of the existing row that was bumped.</summary>
    public long Add(ClipboardItem item)
    {
        string hash = _cipher.DedupHash(ComputeCanonical(item));

        using (SqliteCommand find = _conn.CreateCommand())
        {
            find.CommandText = "SELECT id FROM items WHERE content_hash = $h LIMIT 1";
            find.Parameters.AddWithValue("$h", hash);
            object? existing = find.ExecuteScalar();
            if (existing != null && existing != DBNull.Value)
            {
                long existingId = Convert.ToInt64(existing, CultureInfo.InvariantCulture);
                Touch(existingId);
                return existingId;
            }
        }

        long newId;
        using (SqliteCommand insert = _conn.CreateCommand())
        {
            insert.CommandText = @"
                INSERT INTO items (type, text, image, preview, source_app, pinned, content_hash, created_at, html, rtf, ocr_text, updated_ms, sync_dirty)
                VALUES ($type, $text, $image, $preview, $src, 0, $hash, $created, $html, $rtf, $ocr, $ums, 1);
                SELECT last_insert_rowid();";
            insert.Parameters.AddWithValue("$type", (int)item.Type);
            insert.Parameters.AddWithValue("$text", (object?)_cipher.EncryptText(item.Text) ?? DBNull.Value);
            insert.Parameters.AddWithValue("$image", (object?)_cipher.EncryptBlob(item.ImageData) ?? DBNull.Value);
            insert.Parameters.AddWithValue("$preview", _cipher.EncryptText(item.Preview) ?? string.Empty);
            insert.Parameters.AddWithValue("$src", (object?)item.SourceApp ?? DBNull.Value);
            insert.Parameters.AddWithValue("$hash", hash);
            insert.Parameters.AddWithValue("$created", ToIso(DateTime.UtcNow));
            insert.Parameters.AddWithValue("$html", (object?)_cipher.EncryptText(item.Html) ?? DBNull.Value);
            insert.Parameters.AddWithValue("$rtf", (object?)_cipher.EncryptText(item.Rtf) ?? DBNull.Value);
            insert.Parameters.AddWithValue("$ocr", (object?)_cipher.EncryptText(item.OcrText) ?? DBNull.Value);
            insert.Parameters.AddWithValue("$ums", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            newId = Convert.ToInt64(insert.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        TrimOverflow();
        return newId;
    }

    /// <summary>One-time: encrypt any rows left plaintext by a pre-encryption build.</summary>
    private void MigrateEncryption()
    {
        if (GetSetting(EncVersionKey) == "1")
        {
            return;
        }

        var rows = new List<(long Id, int Type, string? Text, byte[]? Image, string Preview)>();
        using (SqliteCommand read = _conn.CreateCommand())
        {
            read.CommandText = "SELECT id, type, text, image, preview FROM items";
            using SqliteDataReader reader = read.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((
                    reader.GetInt64(0),
                    reader.GetInt32(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.IsDBNull(3) ? null : (byte[])reader.GetValue(3),
                    reader.GetString(4)));
            }
        }

        foreach (var row in rows)
        {
            var item = new ClipboardItem { Type = (ClipItemType)row.Type, Text = row.Text, ImageData = row.Image };
            using SqliteCommand up = _conn.CreateCommand();
            up.CommandText = "UPDATE items SET text = $t, image = $i, preview = $p, content_hash = $h WHERE id = $id";
            up.Parameters.AddWithValue("$t", (object?)_cipher.EncryptText(row.Text) ?? DBNull.Value);
            up.Parameters.AddWithValue("$i", (object?)_cipher.EncryptBlob(row.Image) ?? DBNull.Value);
            up.Parameters.AddWithValue("$p", _cipher.EncryptText(row.Preview) ?? string.Empty);
            up.Parameters.AddWithValue("$h", _cipher.DedupHash(ComputeCanonical(item)));
            up.Parameters.AddWithValue("$id", row.Id);
            up.ExecuteNonQuery();
        }

        SetSetting(EncVersionKey, "1");
    }

    public List<ClipboardItem> GetAll(int limit = 200)
    {
        using SqliteCommand cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, type, text, image, preview, source_app, pinned, created_at, html, rtf, ocr_text
            FROM items
            ORDER BY pinned DESC, created_at DESC
            LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit", limit);
        return ReadAll(cmd);
    }

    public List<ClipboardItem> Search(string query, int limit = 200)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return GetAll(limit);
        }

        // Content is encrypted at rest, so SQL LIKE can't match — decrypt and filter in memory.
        string q = query.Trim();
        return GetAll(100000)
            .Where(i => (i.Text != null && i.Text.Contains(q, StringComparison.OrdinalIgnoreCase))
                     || i.Preview.Contains(q, StringComparison.OrdinalIgnoreCase)
                     || (i.OcrText != null && i.OcrText.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .Take(limit)
            .ToList();
    }

    /// <summary>Stores recognized (OCR) text for an image row after async recognition completes.</summary>
    public void UpdateOcrText(long id, string? text)
    {
        using SqliteCommand cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE items SET ocr_text = $o, updated_ms = $ums, sync_dirty = 1 WHERE id = $id";
        cmd.Parameters.AddWithValue("$o", (object?)_cipher.EncryptText(text) ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$ums", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    // ---- Cross-device sync bookkeeping ----

    /// <summary>A text/image item that has local changes to push.</summary>
    public List<ClipboardItem> GetDirtyForSync(int limit = 200)
    {
        using SqliteCommand cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, type, text, image, preview, source_app, pinned, created_at, html, rtf, ocr_text, sync_uuid, updated_ms
            FROM items
            WHERE sync_dirty = 1 AND type IN ($t, $i)
            ORDER BY updated_ms ASC LIMIT $limit";
        cmd.Parameters.AddWithValue("$t", (int)ClipItemType.Text);
        cmd.Parameters.AddWithValue("$i", (int)ClipItemType.Image);
        cmd.Parameters.AddWithValue("$limit", limit);
        return ReadAllWithSync(cmd);
    }

    public void AssignSyncUuid(long id, string uuid)
    {
        using SqliteCommand cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE items SET sync_uuid = $u WHERE id = $id";
        cmd.Parameters.AddWithValue("$u", uuid);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void MarkSynced(long id)
    {
        using SqliteCommand cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE items SET sync_dirty = 0 WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Marks every text/image item dirty (used when sync is first enabled).</summary>
    public void MarkAllDirty()
    {
        using SqliteCommand cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE items SET sync_dirty = 1 WHERE type IN ($t, $i)";
        cmd.Parameters.AddWithValue("$t", (int)ClipItemType.Text);
        cmd.Parameters.AddWithValue("$i", (int)ClipItemType.Image);
        cmd.ExecuteNonQuery();
    }

    public (long Id, long UpdatedMs)? FindBySyncUuid(string uuid)
    {
        using SqliteCommand cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, updated_ms FROM items WHERE sync_uuid = $u LIMIT 1";
        cmd.Parameters.AddWithValue("$u", uuid);
        using SqliteDataReader r = cmd.ExecuteReader();
        return r.Read() ? (r.GetInt64(0), r.GetInt64(1)) : null;
    }

    /// <summary>Applies a pulled remote item locally (last-write-wins by updated_ms).</summary>
    public void UpsertFromSync(ClipboardItem item, string uuid, long updatedMs)
    {
        (long Id, long UpdatedMs)? existing = FindBySyncUuid(uuid);
        if (existing is { } e)
        {
            if (updatedMs <= e.UpdatedMs)
            {
                return; // local is same or newer
            }
            using SqliteCommand up = _conn.CreateCommand();
            up.CommandText = @"UPDATE items SET text=$text, image=$image, preview=$preview, html=$html, rtf=$rtf,
                ocr_text=$ocr, updated_ms=$ums, sync_dirty=0 WHERE id=$id";
            up.Parameters.AddWithValue("$text", (object?)_cipher.EncryptText(item.Text) ?? DBNull.Value);
            up.Parameters.AddWithValue("$image", (object?)_cipher.EncryptBlob(item.ImageData) ?? DBNull.Value);
            up.Parameters.AddWithValue("$preview", _cipher.EncryptText(item.Preview) ?? string.Empty);
            up.Parameters.AddWithValue("$html", (object?)_cipher.EncryptText(item.Html) ?? DBNull.Value);
            up.Parameters.AddWithValue("$rtf", (object?)_cipher.EncryptText(item.Rtf) ?? DBNull.Value);
            up.Parameters.AddWithValue("$ocr", (object?)_cipher.EncryptText(item.OcrText) ?? DBNull.Value);
            up.Parameters.AddWithValue("$ums", updatedMs);
            up.Parameters.AddWithValue("$id", e.Id);
            up.ExecuteNonQuery();
            return;
        }

        using SqliteCommand insert = _conn.CreateCommand();
        insert.CommandText = @"
            INSERT INTO items (type, text, image, preview, source_app, pinned, content_hash, created_at, html, rtf, ocr_text, sync_uuid, updated_ms, sync_dirty)
            VALUES ($type, $text, $image, $preview, NULL, 0, $hash, $created, $html, $rtf, $ocr, $uuid, $ums, 0)";
        insert.Parameters.AddWithValue("$type", (int)item.Type);
        insert.Parameters.AddWithValue("$text", (object?)_cipher.EncryptText(item.Text) ?? DBNull.Value);
        insert.Parameters.AddWithValue("$image", (object?)_cipher.EncryptBlob(item.ImageData) ?? DBNull.Value);
        insert.Parameters.AddWithValue("$preview", _cipher.EncryptText(item.Preview) ?? string.Empty);
        insert.Parameters.AddWithValue("$hash", _cipher.DedupHash(ComputeCanonical(item)));
        insert.Parameters.AddWithValue("$created", ToIso(DateTimeOffset.FromUnixTimeMilliseconds(updatedMs).UtcDateTime));
        insert.Parameters.AddWithValue("$html", (object?)_cipher.EncryptText(item.Html) ?? DBNull.Value);
        insert.Parameters.AddWithValue("$rtf", (object?)_cipher.EncryptText(item.Rtf) ?? DBNull.Value);
        insert.Parameters.AddWithValue("$ocr", (object?)_cipher.EncryptText(item.OcrText) ?? DBNull.Value);
        insert.Parameters.AddWithValue("$uuid", uuid);
        insert.Parameters.AddWithValue("$ums", updatedMs);
        insert.ExecuteNonQuery();
    }

    /// <summary>Deletes a local item that was tombstoned remotely (no new tombstone created).</summary>
    public void DeleteBySyncUuid(string uuid)
    {
        using SqliteCommand cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM items WHERE sync_uuid = $u";
        cmd.Parameters.AddWithValue("$u", uuid);
        cmd.ExecuteNonQuery();
    }

    public void AddTombstone(string uuid)
    {
        using SqliteCommand cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO sync_tombstones (sync_uuid, updated_ms) VALUES ($u, $ms)";
        cmd.Parameters.AddWithValue("$u", uuid);
        cmd.Parameters.AddWithValue("$ms", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.ExecuteNonQuery();
    }

    public List<(string Uuid, long UpdatedMs)> GetTombstones()
    {
        var list = new List<(string, long)>();
        using SqliteCommand cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT sync_uuid, updated_ms FROM sync_tombstones";
        using SqliteDataReader r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add((r.GetString(0), r.GetInt64(1)));
        }
        return list;
    }

    public void RemoveTombstone(string uuid)
    {
        using SqliteCommand cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM sync_tombstones WHERE sync_uuid = $u";
        cmd.Parameters.AddWithValue("$u", uuid);
        cmd.ExecuteNonQuery();
    }

    private List<ClipboardItem> ReadAllWithSync(SqliteCommand cmd)
    {
        var list = new List<ClipboardItem>();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new ClipboardItem
            {
                Id = reader.GetInt64(0),
                Type = (ClipItemType)reader.GetInt32(1),
                Text = _cipher.DecryptText(reader.IsDBNull(2) ? null : reader.GetString(2)),
                ImageData = _cipher.DecryptBlob(reader.IsDBNull(3) ? null : (byte[])reader.GetValue(3)),
                Preview = _cipher.DecryptText(reader.GetString(4)) ?? string.Empty,
                SourceApp = reader.IsDBNull(5) ? null : reader.GetString(5),
                Pinned = reader.GetInt32(6) != 0,
                CreatedAt = FromIso(reader.GetString(7)),
                Html = _cipher.DecryptText(reader.IsDBNull(8) ? null : reader.GetString(8)),
                Rtf = _cipher.DecryptText(reader.IsDBNull(9) ? null : reader.GetString(9)),
                OcrText = _cipher.DecryptText(reader.IsDBNull(10) ? null : reader.GetString(10)),
                SyncUuid = reader.IsDBNull(11) ? null : reader.GetString(11),
                UpdatedMs = reader.GetInt64(12),
            });
        }
        return list;
    }

    public ClipboardItem? GetMostRecentText()
    {
        using SqliteCommand cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, type, text, image, preview, source_app, pinned, created_at, html, rtf, ocr_text
            FROM items
            WHERE type = $t
            ORDER BY created_at DESC
            LIMIT 1";
        cmd.Parameters.AddWithValue("$t", (int)ClipItemType.Text);
        List<ClipboardItem> rows = ReadAll(cmd);
        return rows.Count > 0 ? rows[0] : null;
    }

    /// <summary>Bumps an item's timestamp to now (move-to-front).</summary>
    public void Touch(long id)
    {
        using SqliteCommand cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE items SET created_at = $now WHERE id = $id";
        cmd.Parameters.AddWithValue("$now", ToIso(DateTime.UtcNow));
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void TogglePin(long id)
    {
        using SqliteCommand cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE items SET pinned = CASE WHEN pinned = 1 THEN 0 ELSE 1 END WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void Delete(long id)
    {
        // If the item was synced, leave a tombstone so the deletion propagates to other devices.
        using (SqliteCommand find = _conn.CreateCommand())
        {
            find.CommandText = "SELECT sync_uuid FROM items WHERE id = $id";
            find.Parameters.AddWithValue("$id", id);
            object? uuid = find.ExecuteScalar();
            if (uuid is string s && s.Length > 0)
            {
                AddTombstone(s);
            }
        }
        using SqliteCommand cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM items WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void Clear(bool keepPinned)
    {
        using SqliteCommand cmd = _conn.CreateCommand();
        cmd.CommandText = keepPinned ? "DELETE FROM items WHERE pinned = 0" : "DELETE FROM items";
        cmd.ExecuteNonQuery();
    }

    public int Count()
    {
        using SqliteCommand cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM items";
        return Convert.ToInt32(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public string? GetSetting(string key)
    {
        using SqliteCommand cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM meta WHERE key = $k LIMIT 1";
        cmd.Parameters.AddWithValue("$k", key);
        object? value = cmd.ExecuteScalar();
        return value == null || value == DBNull.Value ? null : (string)value;
    }

    public void SetSetting(string key, string value)
    {
        using SqliteCommand cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT OR REPLACE INTO meta (key, value) VALUES ($k, $v)";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    // ---- Snippets ----
    public List<Snippet> GetSnippets()
    {
        var list = new List<Snippet>();
        using SqliteCommand cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, text FROM snippets ORDER BY sort, id";
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new Snippet
            {
                Id = reader.GetInt64(0),
                Name = reader.GetString(1),
                Text = reader.GetString(2),
            });
        }
        return list;
    }

    public long AddSnippet(string name, string text)
    {
        using SqliteCommand cmd = _conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO snippets (name, text, sort) VALUES ($n, $t,
            (SELECT COALESCE(MAX(sort), 0) + 1 FROM snippets));
            SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$t", text);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public void UpdateSnippet(long id, string name, string text)
    {
        using SqliteCommand cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE snippets SET name = $n, text = $t WHERE id = $id";
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$t", text);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void DeleteSnippet(long id)
    {
        using SqliteCommand cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM snippets WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    // ---- Password vault (raw encrypted blobs; crypto lives in PasswordVault) ----
    public List<PasswordEntry> GetPasswordEntries()
    {
        var list = new List<PasswordEntry>();
        using SqliteCommand cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, name FROM passwords ORDER BY sort, id";
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new PasswordEntry { Id = reader.GetInt64(0), Name = reader.GetString(1) });
        }
        return list;
    }

    public List<(long Id, string Name, byte[] Blob)> GetPasswordRows()
    {
        var list = new List<(long, string, byte[])>();
        using SqliteCommand cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, name, blob FROM passwords ORDER BY sort, id";
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add((reader.GetInt64(0), reader.GetString(1), (byte[])reader.GetValue(2)));
        }
        return list;
    }

    public byte[]? GetPasswordBlob(long id)
    {
        using SqliteCommand cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT blob FROM passwords WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        object? value = cmd.ExecuteScalar();
        return value is byte[] b ? b : null;
    }

    public long InsertPassword(string name, byte[] blob)
    {
        using SqliteCommand cmd = _conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO passwords (name, blob, sort) VALUES ($n, $b,
            (SELECT COALESCE(MAX(sort), 0) + 1 FROM passwords));
            SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$b", blob);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public void UpdatePassword(long id, string name, byte[] blob)
    {
        using SqliteCommand cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE passwords SET name = $n, blob = $b WHERE id = $id";
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$b", blob);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void DeletePassword(long id)
    {
        using SqliteCommand cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM passwords WHERE id = $id";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    // ---- Custom lists ----
    public List<ClipList> GetLists()
    {
        var list = new List<ClipList>();
        using SqliteCommand cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, name FROM lists ORDER BY sort, id";
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new ClipList { Id = reader.GetInt64(0), Name = reader.GetString(1) });
        }
        return list;
    }

    public long AddList(string name)
    {
        using SqliteCommand cmd = _conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO lists (name, sort) VALUES ($n,
            (SELECT COALESCE(MAX(sort), 0) + 1 FROM lists));
            SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$n", name);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public void RenameList(long id, string name)
    {
        using SqliteCommand cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE lists SET name = $n WHERE id = $id";
        cmd.Parameters.AddWithValue("$n", name);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void DeleteList(long id)
    {
        using SqliteCommand cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE items SET list_id = NULL WHERE list_id = $id; DELETE FROM lists WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public List<ClipboardItem> GetByList(long listId)
    {
        using SqliteCommand cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, type, text, image, preview, source_app, pinned, created_at, html, rtf, ocr_text
            FROM items WHERE list_id = $id
            ORDER BY pinned DESC, created_at DESC";
        cmd.Parameters.AddWithValue("$id", listId);
        return ReadAll(cmd);
    }

    public void AssignToList(long itemId, long? listId)
    {
        using SqliteCommand cmd = _conn.CreateCommand();
        cmd.CommandText = "UPDATE items SET list_id = $l WHERE id = $id";
        cmd.Parameters.AddWithValue("$l", (object?)listId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$id", itemId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Deletes unpinned items older than <paramref name="days"/>. No-op when days &lt;= 0.</summary>
    public void PurgeExpired(int days)
    {
        if (days <= 0)
        {
            return;
        }

        using SqliteCommand cmd = _conn.CreateCommand();
        cmd.CommandText = "DELETE FROM items WHERE pinned = 0 AND created_at < $cutoff";
        cmd.Parameters.AddWithValue("$cutoff", ToIso(DateTime.UtcNow.AddDays(-days)));
        cmd.ExecuteNonQuery();
    }

    private void TrimOverflow()
    {
        using SqliteCommand cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            DELETE FROM items
            WHERE pinned = 0 AND list_id IS NULL AND id NOT IN (
                SELECT id FROM items WHERE pinned = 0 AND list_id IS NULL ORDER BY created_at DESC LIMIT $keep
            )";
        cmd.Parameters.AddWithValue("$keep", GetMaxItems());
        cmd.ExecuteNonQuery();
    }

    private int GetMaxItems()
    {
        if (int.TryParse(GetSetting(MaxItemsKey), out int n) && n >= 50 && n <= 100000)
        {
            return n;
        }
        return MaxUnpinnedItems;
    }

    private List<ClipboardItem> ReadAll(SqliteCommand cmd)
    {
        var list = new List<ClipboardItem>();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new ClipboardItem
            {
                Id = reader.GetInt64(0),
                Type = (ClipItemType)reader.GetInt32(1),
                Text = _cipher.DecryptText(reader.IsDBNull(2) ? null : reader.GetString(2)),
                ImageData = _cipher.DecryptBlob(reader.IsDBNull(3) ? null : (byte[])reader.GetValue(3)),
                Preview = _cipher.DecryptText(reader.GetString(4)) ?? string.Empty,
                SourceApp = reader.IsDBNull(5) ? null : reader.GetString(5),
                Pinned = reader.GetInt32(6) != 0,
                CreatedAt = FromIso(reader.GetString(7)),
                Html = _cipher.DecryptText(reader.IsDBNull(8) ? null : reader.GetString(8)),
                Rtf = _cipher.DecryptText(reader.IsDBNull(9) ? null : reader.GetString(9)),
                OcrText = _cipher.DecryptText(reader.IsDBNull(10) ? null : reader.GetString(10)),
            });
        }
        return list;
    }

    private static string ToIso(DateTime utc) => utc.ToString("o", CultureInfo.InvariantCulture);

    private static DateTime FromIso(string s) =>
        DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    /// <summary>Canonical plaintext form fed to the keyed dedup hash.</summary>
    private static string ComputeCanonical(ClipboardItem item)
    {
        string body = item.Type == ClipItemType.Image
            ? Convert.ToHexString(SHA256.HashData(item.ImageData ?? Array.Empty<byte>()))
            : (item.Text ?? string.Empty);
        return ((int)item.Type).ToString(CultureInfo.InvariantCulture) + ":" + body;
    }

    public void Dispose()
    {
        _conn.Close();
        _conn.Dispose();
    }
}
