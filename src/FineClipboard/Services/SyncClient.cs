using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace FineClipboard.Services;

/// <summary>Thin HTTP client for the finepaste-server sync API.</summary>
public sealed class SyncClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public string BaseUrl { get; set; } = string.Empty;
    public string? Token { get; set; }

    public sealed class AuthResult
    {
        [JsonPropertyName("token")] public string Token { get; set; } = string.Empty;
        [JsonPropertyName("vip")] public bool Vip { get; set; }
        [JsonPropertyName("vip_until")] public long? VipUntil { get; set; }
    }

    public sealed class PushItem
    {
        [JsonPropertyName("uuid")] public string Uuid { get; set; } = string.Empty;
        [JsonPropertyName("kind")] public int Kind { get; set; }
        [JsonPropertyName("updated_at")] public long UpdatedAt { get; set; }
        [JsonPropertyName("deleted")] public bool Deleted { get; set; }
        [JsonPropertyName("cipher")] public string Cipher { get; set; } = string.Empty;
    }

    public sealed class PushResult
    {
        [JsonPropertyName("cursor")] public long Cursor { get; set; }
    }

    public sealed class Change
    {
        [JsonPropertyName("uuid")] public string Uuid { get; set; } = string.Empty;
        [JsonPropertyName("kind")] public int Kind { get; set; }
        [JsonPropertyName("seq")] public long Seq { get; set; }
        [JsonPropertyName("updated_at")] public long UpdatedAt { get; set; }
        [JsonPropertyName("deleted")] public bool Deleted { get; set; }
        [JsonPropertyName("blob_size")] public int BlobSize { get; set; }
        [JsonPropertyName("cipher")] public string? Cipher { get; set; }
    }

    public sealed class ChangesResult
    {
        [JsonPropertyName("cursor")] public long Cursor { get; set; }
        [JsonPropertyName("has_more")] public bool HasMore { get; set; }
        [JsonPropertyName("changes")] public List<Change> Changes { get; set; } = new();
    }

    public Task<AuthResult?> RegisterAsync(string email, string password) =>
        PostAuthAsync("/api/register", email, password);

    public Task<AuthResult?> LoginAsync(string email, string password) =>
        PostAuthAsync("/api/login", email, password);

    private async Task<AuthResult?> PostAuthAsync(string path, string email, string password)
    {
        using HttpResponseMessage resp = await Http.PostAsJsonAsync(
            BaseUrl + path, new { email, password }).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new SyncException(await ErrorMessageAsync(resp).ConfigureAwait(false));
        }
        return await resp.Content.ReadFromJsonAsync<AuthResult>().ConfigureAwait(false);
    }

    public async Task<AuthResult?> RedeemAsync(string key)
    {
        using HttpRequestMessage req = Authed(HttpMethod.Post, "/api/redeem");
        req.Content = JsonContent.Create(new { key });
        using HttpResponseMessage resp = await Http.SendAsync(req).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new SyncException(await ErrorMessageAsync(resp).ConfigureAwait(false));
        }
        return await resp.Content.ReadFromJsonAsync<AuthResult>().ConfigureAwait(false);
    }

    public async Task<PushResult> PushAsync(int retentionDays, IEnumerable<PushItem> items)
    {
        using HttpRequestMessage req = Authed(HttpMethod.Post, "/api/sync/push");
        req.Content = JsonContent.Create(new { retention_days = retentionDays, items });
        using HttpResponseMessage resp = await Http.SendAsync(req).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new SyncException(await ErrorMessageAsync(resp).ConfigureAwait(false));
        }
        return (await resp.Content.ReadFromJsonAsync<PushResult>().ConfigureAwait(false))!;
    }

    public async Task<ChangesResult> ChangesAsync(long since, int limit = 200)
    {
        using HttpRequestMessage req = Authed(HttpMethod.Get, $"/api/sync/changes?since={since}&limit={limit}");
        using HttpResponseMessage resp = await Http.SendAsync(req).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
        {
            throw new SyncException(await ErrorMessageAsync(resp).ConfigureAwait(false));
        }
        return (await resp.Content.ReadFromJsonAsync<ChangesResult>().ConfigureAwait(false))!;
    }

    public async Task<byte[]?> BlobAsync(string uuid)
    {
        using HttpRequestMessage req = Authed(HttpMethod.Get, $"/api/sync/blob/{uuid}");
        using HttpResponseMessage resp = await Http.SendAsync(req).ConfigureAwait(false);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        if (!resp.IsSuccessStatusCode)
        {
            throw new SyncException(await ErrorMessageAsync(resp).ConfigureAwait(false));
        }
        return await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
    }

    private HttpRequestMessage Authed(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, BaseUrl + path);
        if (!string.IsNullOrEmpty(Token))
        {
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
        }
        return req;
    }

    private static async Task<string> ErrorMessageAsync(HttpResponseMessage resp)
    {
        try
        {
            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out JsonElement e))
            {
                return e.GetString() ?? resp.StatusCode.ToString();
            }
        }
        catch
        {
            // fall through
        }
        return $"HTTP {(int)resp.StatusCode}";
    }
}

/// <summary>A sync operation failed; Message carries a user-facing reason.</summary>
public sealed class SyncException : Exception
{
    public SyncException(string message) : base(message) { }
}
