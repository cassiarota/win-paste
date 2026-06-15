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
            );";
        cmd.ExecuteNonQuery();
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
            WHERE pinned = 0 AND id NOT IN (
                SELECT id FROM items WHERE pinned = 0 ORDER BY created_at DESC LIMIT $keep
            )";
        cmd.Parameters.AddWithValue("$keep", MaxUnpinnedItems);
        cmd.ExecuteNonQuery();
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
