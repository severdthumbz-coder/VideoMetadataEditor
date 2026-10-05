using System.Net.Http;
using System.Text.RegularExpressions;

namespace VideoMetadataEditor.Services;

/// <summary>
/// Result of an update check: whether a newer release exists, and where to get it.
/// </summary>
public sealed record UpdateInfo(bool IsNewer, Version LatestVersion, string ReleaseUrl);

/// <summary>Why an update check ended the way it did — so "no badge" is never ambiguous.</summary>
public enum UpdateCheckStatus
{
    UpdateAvailable, // a newer release exists (Update is set)
    UpToDate,        // the newest release is this build or older
    NoReleases,      // the repo is public but has no published releases
    NotVisible,      // GitHub answered 404: the repo is private (anonymous checks can't see it) or doesn't exist
    RateLimited,     // GitHub refused: anonymous API limit reached (RateLimitResetsAt may be set)
    Unreachable      // no network, timeout, DNS, or an unexpected response
}

/// <summary>
/// Detailed outcome of an update check. <see cref="Latest"/> is the newest published
/// version when it could be determined; <see cref="Detail"/> is a short human-readable
/// reason for the log.
/// </summary>
public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    UpdateInfo?       Update,
    Version?          Latest,
    DateTimeOffset?   RateLimitResetsAt,
    string            Detail);

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
    /// Plain web address that GitHub redirects to the newest release's page
    /// (…/releases/tag/v1.4.0.163). Reading the redirect target is an ordinary web
    /// request, not an API call, so it does NOT count against GitHub's 60-requests-per-hour
    /// anonymous API limit. This is the primary check; the API is the fallback.
    /// </summary>
    public const string LatestRedirectUrl =
        "https://github.com/severdthumbz-coder/VideoMetadataEditor/releases/latest";

    /// <summary>
    /// Extracts the release tag from a GitHub redirect target such as
    /// "https://github.com/owner/repo/releases/tag/v1.4.0.163". Returns null when the
    /// target isn't a /releases/tag/… address (e.g. the repo has no releases and GitHub
    /// redirects to the plain /releases page instead). Pure — no network.
    /// </summary>
    public static string? ParseTagFromLocation(string? location)
    {
        if (string.IsNullOrWhiteSpace(location)) return null;
        const string marker = "/releases/tag/";
        var idx = location.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var tag = location[(idx + marker.Length)..];
        // Drop any trailing slash, query string or fragment.
        var cut = tag.IndexOfAny(new[] { '/', '?', '#' });
        if (cut >= 0) tag = tag[..cut];
        tag = Uri.UnescapeDataString(tag).Trim();
        return tag.Length == 0 ? null : tag;
    }

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
    /// Returns the UpdateInfo only when a newer release exists, otherwise null. Kept for
    /// callers that only need yes/no; <see cref="CheckDetailedAsync"/> also says why.
    /// </summary>
    public static async Task<UpdateInfo?> CheckAsync(Version current, CancellationToken ct = default)
    {
        if (current == null) return null;
        var result = await CheckDetailedAsync(current, ct).ConfigureAwait(false);
        return result.Update;
    }

    /// <summary>
    /// Full update check with a clear outcome. Tries the redirect address first (not
    /// subject to the API rate limit), then falls back to the API. Never throws.
    /// </summary>
    public static async Task<UpdateCheckResult> CheckDetailedAsync(Version current, CancellationToken ct = default)
    {
        // ── 1. Redirect: github.com/…/releases/latest → …/releases/tag/<tag> ──────
        string? redirectProblem;
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.Add("User-Agent", "VideoMetadataEditor-UpdateCheck");
            using var resp = await http.GetAsync(
                LatestRedirectUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            var code = (int)resp.StatusCode;
            if (code >= 300 && code < 400 && resp.Headers.Location != null)
            {
                var location = resp.Headers.Location.IsAbsoluteUri
                    ? resp.Headers.Location
                    : new Uri(new Uri(LatestRedirectUrl), resp.Headers.Location);
                var tag = ParseTagFromLocation(location.ToString());
                if (tag == null)
                    return new UpdateCheckResult(UpdateCheckStatus.NoReleases, null, null, null,
                        "the repository has no published releases");

                var remote = ParseTag(tag);
                if (remote != null)
                    return Compare(remote, current, location.ToString(), "via release redirect");

                redirectProblem = $"unrecognised release tag \"{tag}\"";
            }
            else if (code == 404)
            {
                // The releases page isn't rate-limited, so a 404 here is authoritative:
                // the repo is private (or gone). No point asking the API as well.
                return new UpdateCheckResult(UpdateCheckStatus.NotVisible, null, null, null,
                    "GitHub returned 404 — the repository is private or doesn't exist, so anonymous update checks can't see its releases");
            }
            else
            {
                redirectProblem = $"unexpected response {code} from the releases page";
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            redirectProblem = ex is TaskCanceledException ? "timed out" : ex.Message;
        }

        // ── 2. Fallback: the GitHub API ─────────────────────────────────────────────
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.Add("User-Agent", "VideoMetadataEditor-UpdateCheck");
            using var resp = await http.GetAsync(ReleasesApi, ct).ConfigureAwait(false);

            var code = (int)resp.StatusCode;
            if (code == 403 || code == 429)
            {
                DateTimeOffset? resetsAt = null;
                if (resp.Headers.TryGetValues("X-RateLimit-Reset", out var vals)
                    && long.TryParse(vals.FirstOrDefault(), out var unix))
                    resetsAt = DateTimeOffset.FromUnixTimeSeconds(unix);
                return new UpdateCheckResult(UpdateCheckStatus.RateLimited, null, null, resetsAt,
                    "GitHub's anonymous request limit was reached");
            }
            if (code == 404)
                return new UpdateCheckResult(UpdateCheckStatus.NotVisible, null, null, null,
                    "GitHub returned 404 — the repository is private or doesn't exist, so anonymous update checks can't see its releases");
            if (!resp.IsSuccessStatusCode)
                return new UpdateCheckResult(UpdateCheckStatus.Unreachable, null, null, null,
                    $"GitHub returned {code} (releases page: {redirectProblem})");

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var tagMatch = Regex.Match(json, @"""tag_name""\s*:\s*""([^""]+)""");
            var remote = tagMatch.Success ? ParseTag(tagMatch.Groups[1].Value) : null;
            if (remote == null)
                return new UpdateCheckResult(UpdateCheckStatus.Unreachable, null, null, null,
                    "couldn't read the latest release tag");

            var urlMatch = Regex.Match(json, @"""html_url""\s*:\s*""([^""]+)""");
            return Compare(remote, current, urlMatch.Success ? urlMatch.Groups[1].Value : ReleasesPage,
                "via GitHub API");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            var why = ex is TaskCanceledException ? "timed out" : ex.Message;
            return new UpdateCheckResult(UpdateCheckStatus.Unreachable, null, null, null,
                $"couldn't reach GitHub ({why})");
        }
    }

    private static UpdateCheckResult Compare(Version remote, Version current, string url, string how)
        => remote > current
            ? new UpdateCheckResult(UpdateCheckStatus.UpdateAvailable,
                new UpdateInfo(true, remote, url), remote, null, how)
            : new UpdateCheckResult(UpdateCheckStatus.UpToDate, null, remote, null, how);
}
