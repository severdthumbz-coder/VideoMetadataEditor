using System.Net.Http;
using System.Text.RegularExpressions;

namespace VideoMetadataEditor.Services;

/// <summary>
/// Result of an update check: whether a newer release exists, and where to get it.
/// </summary>
public sealed record UpdateInfo(bool IsNewer, Version LatestVersion, string ReleaseUrl);

/// <summary>
/// Checks GitHub Releases for a newer version of the app. Split into a pure,
/// unit-tested decision core (tag parsing + version comparison) and a thin network
/// fetch. Everything fails silent — offline, private repo, no releases yet, malformed
/// tag all simply yield null / "not newer", never an exception the caller must handle.
/// </summary>
public static class UpdateCheckService
{
    public const string ReleasesApi =
        "https://api.github.com/repos/severdthumbz-coder/VideoMetadataEditor/releases/latest";
    public const string ReleasesPage =
        "https://github.com/severdthumbz-coder/VideoMetadataEditor/releases";

    /// <summary>
    /// Parses a GitHub release tag into a Version, tolerating a leading 'v' and
    /// surrounding whitespace (e.g. "v1.4.0.130", " 1.4.0.130 "). Returns null for
    /// anything that isn't a valid dotted version.
    /// </summary>
    public static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var cleaned = tag.Trim().TrimStart('v', 'V').Trim();
        return Version.TryParse(cleaned, out var v) ? v : null;
    }

    /// <summary>
    /// Extracts tag_name and html_url from a GitHub /releases/latest JSON body and
    /// compares the tag to the running version. Pure — no network. Returns an
    /// UpdateInfo when the remote is strictly newer, otherwise null.
    /// </summary>
    public static UpdateInfo? Evaluate(string json, Version current)
    {
        if (string.IsNullOrWhiteSpace(json) || current == null) return null;

        var tagMatch = Regex.Match(json, @"""tag_name""\s*:\s*""([^""]+)""");
        if (!tagMatch.Success) return null;

        var remote = ParseTag(tagMatch.Groups[1].Value);
        if (remote == null) return null;

        // Strictly newer only — equal or older means no update.
        if (remote <= current) return null;

        var urlMatch = Regex.Match(json, @"""html_url""\s*:\s*""([^""]+)""");
        var url = urlMatch.Success ? urlMatch.Groups[1].Value : ReleasesPage;

        return new UpdateInfo(true, remote, url);
    }

    /// <summary>
    /// Fetches the latest release from GitHub and evaluates it against <paramref name="current"/>.
    /// Fails silent: returns null on any network error, timeout, missing release, or
    /// unparseable response. Never throws to the caller.
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken ct = default)
    {
        if (current == null) return null;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            http.DefaultRequestHeaders.Add("User-Agent", "VideoMetadataEditor-UpdateCheck");
            var json = await http.GetStringAsync(ReleasesApi, ct).ConfigureAwait(false);
            return Evaluate(json, current);
        }
        catch
        {
            // Non-fatal: no internet, private repo, no releases yet, rate-limited, etc.
            return null;
        }
    }
}
