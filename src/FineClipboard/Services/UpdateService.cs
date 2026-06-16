using System;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace FineClipboard.Services;

/// <summary>Details of an available update.</summary>
public sealed record UpdateInfo(string Version, string Url);

/// <summary>Outcome of an update check, distinguishing a real failure from "already current".</summary>
public enum UpdateStatus { UpToDate, UpdateAvailable, Failed }

/// <summary>Result of an update check: the status plus, when available, the new version's info.</summary>
public sealed record UpdateCheckResult(UpdateStatus Status, UpdateInfo? Update)
{
    public static readonly UpdateCheckResult UpToDate = new(UpdateStatus.UpToDate, null);
    public static readonly UpdateCheckResult Failed = new(UpdateStatus.Failed, null);
    public static UpdateCheckResult Available(UpdateInfo info) => new(UpdateStatus.UpdateAvailable, info);
}

/// <summary>
/// Checks the GitHub Releases API for a newer version. No token is used, so the repo must be
/// public for this to return results. A network/API/parse failure returns <see cref="UpdateStatus.Failed"/>
/// (NOT "up to date"), so callers never tell the user they're current when the check never ran.
/// </summary>
public sealed class UpdateService
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/cassiarota/fine-clipboard/releases/latest";
    private const string LatestReleasePage =
        "https://github.com/cassiarota/fine-clipboard/releases/latest";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public async Task<UpdateCheckResult> CheckAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
            request.Headers.UserAgent.ParseAdd("FineClipboard-UpdateCheck");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using HttpResponseMessage response = await Http.SendAsync(request).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return UpdateCheckResult.Failed;
            }

            string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            using JsonDocument doc = JsonDocument.Parse(json);

            string tag = doc.RootElement.TryGetProperty("tag_name", out JsonElement t) ? t.GetString() ?? "" : "";
            string url = doc.RootElement.TryGetProperty("html_url", out JsonElement u) ? u.GetString() ?? "" : "";

            Version? latest = ParseVersion(tag);
            if (latest == null)
            {
                return UpdateCheckResult.Failed; // couldn't read the release version
            }

            Version current = Normalize(Assembly.GetExecutingAssembly().GetName().Version);
            if (latest > current)
            {
                string download = string.IsNullOrEmpty(url) ? LatestReleasePage : url;
                return UpdateCheckResult.Available(new UpdateInfo(tag, download));
            }
            return UpdateCheckResult.UpToDate;
        }
        catch
        {
            return UpdateCheckResult.Failed;
        }
    }

    private static Version? ParseVersion(string tag)
    {
        string s = tag.TrimStart('v', 'V').Trim();
        return Version.TryParse(s, out Version? v) ? Normalize(v) : null;
    }

    /// <summary>Reduces a version to Major.Minor.Build so 0.2.0 and 0.2.0.0 compare equal.</summary>
    private static Version Normalize(Version? v)
    {
        if (v == null)
        {
            return new Version(0, 0, 0);
        }
        return new Version(v.Major, v.Minor, v.Build < 0 ? 0 : v.Build);
    }
}
