using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using PasteNowWin.Models;

namespace PasteNowWin.Services;

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

    private readonly SqliteConnection _conn;

    public HistoryStore()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PasteNowWin");
        Directory.CreateDirectory(dir);
        string dbPath = Path.Combine(dir, "history.db");

        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        Initialize();
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
            );";
        cmd.ExecuteNonQuery();

        EnsureItemsListIdColumn();
    }

    /// <summary>Adds items.list_id to databases created before custom lists existed.</summary>
    private void EnsureItemsListIdColumn()
    {
        bool exists = false;
        using (SqliteCommand info = _conn.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(items)";
            using SqliteDataReader reader = info.ExecuteReader();
            while (reader.Read())
            {
                if (reader.GetString(1) == "list_id")
                {
                    exists = true;
                    break;
                }
            }
        }
        if (!exists)
        {
            using SqliteCommand alter = _conn.CreateCommand();
            alter.CommandText = "ALTER TABLE items ADD COLUMN list_id INTEGER";
            alter.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Adds an item. If identical content already exists, bumps it to the top
    /// (move-to-front) instead of inserting a duplicate.
    /// </summary>
    public void Add(ClipboardItem item)
    {
        string hash = ComputeHash(item);

        using (SqliteCommand find = _conn.CreateCommand())
        {
            find.CommandText = "SELECT id FROM items WHERE content_hash = $h LIMIT 1";
            find.Parameters.AddWithValue("$h", hash);
            object? existing = find.ExecuteScalar();
            if (existing != null && existing != DBNull.Value)
            {
                Touch(Convert.ToInt64(existing, CultureInfo.InvariantCulture));
                return;
            }
        }

        using (SqliteCommand insert = _conn.CreateCommand())
        {
            insert.CommandText = @"
                INSERT INTO items (type, text, image, preview, source_app, pinned, content_hash, created_at)
                VALUES ($type, $text, $image, $preview, $src, 0, $hash, $created)";
            insert.Parameters.AddWithValue("$type", (int)item.Type);
            insert.Parameters.AddWithValue("$text", (object?)item.Text ?? DBNull.Value);
            insert.Parameters.AddWithValue("$image", (object?)item.ImageData ?? DBNull.Value);
            insert.Parameters.AddWithValue("$preview", item.Preview);
            insert.Parameters.AddWithValue("$src", (object?)item.SourceApp ?? DBNull.Value);
            insert.Parameters.AddWithValue("$hash", hash);
            insert.Parameters.AddWithValue("$created", ToIso(DateTime.UtcNow));
            insert.ExecuteNonQuery();
        }

        TrimOverflow();
    }

    public List<ClipboardItem> GetAll(int limit = 200)
    {
        using SqliteCommand cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, type, text, image, preview, source_app, pinned, created_at
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

        using SqliteCommand cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, type, text, image, preview, source_app, pinned, created_at
            FROM items
            WHERE preview LIKE $q OR text LIKE $q
            ORDER BY pinned DESC, created_at DESC
            LIMIT $limit";
        cmd.Parameters.AddWithValue("$q", "%" + query + "%");
        cmd.Parameters.AddWithValue("$limit", limit);
        return ReadAll(cmd);
    }

    public ClipboardItem? GetMostRecentText()
    {
        using SqliteCommand cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT id, type, text, image, preview, source_app, pinned, created_at
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
            SELECT id, type, text, image, preview, source_app, pinned, created_at
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

    private static List<ClipboardItem> ReadAll(SqliteCommand cmd)
    {
        var list = new List<ClipboardItem>();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new ClipboardItem
            {
                Id = reader.GetInt64(0),
                Type = (ClipItemType)reader.GetInt32(1),
                Text = reader.IsDBNull(2) ? null : reader.GetString(2),
                ImageData = reader.IsDBNull(3) ? null : (byte[])reader.GetValue(3),
                Preview = reader.GetString(4),
                SourceApp = reader.IsDBNull(5) ? null : reader.GetString(5),
                Pinned = reader.GetInt32(6) != 0,
                CreatedAt = FromIso(reader.GetString(7)),
            });
        }
        return list;
    }

    private static string ToIso(DateTime utc) => utc.ToString("o", CultureInfo.InvariantCulture);

    private static DateTime FromIso(string s) =>
        DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string ComputeHash(ClipboardItem item)
    {
        byte[] data = item.Type switch
        {
            ClipItemType.Image => item.ImageData ?? Array.Empty<byte>(),
            _ => Encoding.UTF8.GetBytes(item.Text ?? string.Empty),
        };
        byte[] hash = SHA256.HashData(data);
        return ((int)item.Type).ToString(CultureInfo.InvariantCulture) + ":" + Convert.ToHexString(hash);
    }

    public void Dispose()
    {
        _conn.Close();
        _conn.Dispose();
    }
}
