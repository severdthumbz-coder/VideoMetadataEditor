using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using VideoMetadataEditor.Models;

namespace VideoMetadataEditor.Services;

/// <summary>
/// Trakt.tv API integration.
///
/// Auth: OAuth2 Device Flow (PIN-based, no browser redirect).
///   1. POST /oauth/device/code  → get device_code, user_code, verification_url
///   2. Show user_code to user — they visit trakt.tv/activate and enter it
///   3. Poll /oauth/device/token until approved or expired
///   4. Store access_token + refresh_token encrypted with DPAPI
///
/// Sync:
///   Push: POST /sync/history  — mark items as watched
///   Pull: GET  /sync/history/movies + /sync/history/shows
///         Match by IMDb ID or TMDB ID against library entries
///
/// Rate limit: 1,000 requests/day for standard accounts.
/// </summary>
public class TraktApiService
{
    private const string BaseUrl    = "https://api.trakt.tv";
    private const string ApiVersion = "2";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
        DefaultRequestHeaders = { }
    };

    static TraktApiService()
    {
        _http.DefaultRequestHeaders.Add("trakt-api-version", ApiVersion);
        // Required — Cloudflare blocks requests without a User-Agent as potential bots
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("VideoMetadataEditor/1.4 (Windows; compatible)");
    }

    // ── Device Flow ───────────────────────────────────────────────────────────

    public record DeviceCodeResult(
        string DeviceCode,
        string UserCode,
        string VerificationUrl,
        int    ExpiresIn,
        int    Interval);

    public async Task<(DeviceCodeResult? result, string? error)> RequestDeviceCodeAsync(
        string clientId, CancellationToken ct = default)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { client_id = clientId });
            var req  = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/oauth/device/code")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            req.Headers.Add("trakt-api-key", clientId);

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var errBody = string.Empty;
                try { errBody = await resp.Content.ReadAsStringAsync(ct); } catch { }
                return (null, $"HTTP {(int)resp.StatusCode} — {errBody}");
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;

            return (new DeviceCodeResult(
                r.GetProperty("device_code").GetString()!,
                r.GetProperty("user_code").GetString()!,
                r.GetProperty("verification_url").GetString()!,
                r.GetProperty("expires_in").GetInt32(),
                r.GetProperty("interval").GetInt32()
            ), null);
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    public record TokenResult(
        string AccessToken,
        string RefreshToken,
        long   ExpiresAt);

    /// <summary>
    /// Polls /oauth/device/token until the user approves or the code expires.
    /// Returns null result + "pending" while waiting; null result + error on failure.
    /// </summary>
    public async Task<(TokenResult? token, string? error)> PollForTokenAsync(
        string clientId, string clientSecret, string deviceCode,
        int intervalSeconds, IProgress<string>? progress, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            code          = deviceCode,
            client_id     = clientId,
            client_secret = clientSecret
        });

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(5, intervalSeconds)), ct);

            try
            {
                var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/oauth/device/token")
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json")
                };
                req.Headers.Add("trakt-api-key", clientId);

                using var resp = await _http.SendAsync(req, ct);

                if (resp.StatusCode == System.Net.HttpStatusCode.OK)
                {
                    var json = await resp.Content.ReadAsStringAsync(ct);
                    using var doc = JsonDocument.Parse(json);
                    var r = doc.RootElement;
                    var expiresIn = r.TryGetProperty("expires_in", out var exp)
                        ? exp.GetInt64() : 7776000L;
                    return (new TokenResult(
                        SecureKeyService.Encrypt(r.GetProperty("access_token").GetString()!),
                        SecureKeyService.Encrypt(r.GetProperty("refresh_token").GetString()!),
                        DateTimeOffset.UtcNow.ToUnixTimeSeconds() + expiresIn
                    ), null);
                }

                if (resp.StatusCode == System.Net.HttpStatusCode.BadRequest)
                {
                    progress?.Report("Waiting for approval…");
                    continue; // still pending
                }

                if (resp.StatusCode == System.Net.HttpStatusCode.Gone)
                    return (null, "Device code expired. Please try again.");

                return (null, $"Unexpected status: {(int)resp.StatusCode}");
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { return (null, ex.Message); }
        }

        return (null, "Cancelled.");
    }

    /// <summary>
    /// Uses the refresh token to get a new access token without requiring the user
    /// to re-authenticate. Call this when TraktTokenExpiry is within 7 days or has passed.
    /// Returns the new TokenResult on success.
    /// </summary>
    public async Task<(TokenResult? token, string? error)> RefreshAccessTokenAsync(
        string clientId, string clientSecret, string encryptedRefreshToken,
        CancellationToken ct = default)
    {
        try
        {
            var refreshToken = SecureKeyService.Decrypt(encryptedRefreshToken);
            if (string.IsNullOrWhiteSpace(refreshToken))
                return (null, "No refresh token stored.");

            var body = JsonSerializer.Serialize(new
            {
                refresh_token = refreshToken,
                client_id     = clientId,
                client_secret = clientSecret,
                grant_type    = "refresh_token"
            });

            var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/oauth/token")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            req.Headers.Add("trakt-api-key", clientId);

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct);
                return (null, $"Refresh failed: HTTP {(int)resp.StatusCode} — {err}");
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var expiresIn = r.TryGetProperty("expires_in", out var exp)
                ? exp.GetInt64() : 7776000L;

            return (new TokenResult(
                SecureKeyService.Encrypt(r.GetProperty("access_token").GetString()!),
                SecureKeyService.Encrypt(r.GetProperty("refresh_token").GetString()!),
                DateTimeOffset.UtcNow.ToUnixTimeSeconds() + expiresIn
            ), null);
        }
        catch (OperationCanceledException) { return (null, "Cancelled."); }
        catch (Exception ex)              { return (null, ex.Message); }
    }

    public async Task<(string? username, string? error)> GetUsernameAsync(
        string clientId, string accessToken, CancellationToken ct = default)
    {
        try
        {
            var req = BuildRequest(HttpMethod.Get, "/users/me", clientId, accessToken);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return (null, $"HTTP {(int)resp.StatusCode}");
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var username = doc.RootElement.GetProperty("username").GetString();
            return (username, null);
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    // ── Sync — Push ───────────────────────────────────────────────────────────

    public async Task<(bool ok, string? error)> MarkWatchedAsync(
        string clientId, string accessToken,
        string? imdbId, string? tmdbId, bool isEpisode,
        int? season = null, int? episode = null,
        DateTime? watchedAt = null, CancellationToken ct = default)
    {
        try
        {
            object item;
            if (isEpisode && season.HasValue && episode.HasValue)
            {
                item = new
                {
                    shows = new[]
                    {
                        new
                        {
                            ids     = BuildIds(imdbId, tmdbId),
                            seasons = new[]
                            {
                                new
                                {
                                    number   = season.Value,
                                    episodes = new[]
                                    {
                                        new
                                        {
                                            number     = episode.Value,
                                            watched_at = (watchedAt ?? DateTime.UtcNow).ToString("o")
                                        }
                                    }
                                }
                            }
                        }
                    }
                };
            }
            else
            {
                item = new
                {
                    movies = new[]
                    {
                        new
                        {
                            watched_at = (watchedAt ?? DateTime.UtcNow).ToString("o"),
                            ids        = BuildIds(imdbId, tmdbId)
                        }
                    }
                };
            }

            var body = JsonSerializer.Serialize(item);
            var req  = BuildRequest(HttpMethod.Post, "/sync/history", clientId, accessToken, body);
            using var resp = await _http.SendAsync(req, ct);
            return resp.IsSuccessStatusCode
                ? (true, null)
                : (false, $"HTTP {(int)resp.StatusCode}");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    // ── Sync — Pull ───────────────────────────────────────────────────────────

    public record TraktWatchedItem(
        string? ImdbId, string? TmdbId,
        bool IsEpisode, int? Season, int? Episode);

    public async Task<(List<TraktWatchedItem>? items, string? error)> GetWatchedHistoryAsync(
        string clientId, string accessToken, CancellationToken ct = default)
    {
        var results = new List<TraktWatchedItem>();

        // Movies
        var (movies, mErr) = await GetWatchedMoviesAsync(clientId, accessToken, ct);
        if (movies != null) results.AddRange(movies);

        // Shows
        var (shows, sErr) = await GetWatchedShowsAsync(clientId, accessToken, ct);
        if (shows != null) results.AddRange(shows);

        var err = mErr ?? sErr;
        return (results.Count > 0 ? results : null, err);
    }

    private async Task<(List<TraktWatchedItem>? items, string? error)> GetWatchedMoviesAsync(
        string clientId, string accessToken, CancellationToken ct)
    {
        try
        {
            var req  = BuildRequest(HttpMethod.Get, "/sync/watched/movies", clientId, accessToken);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return (null, $"Movies HTTP {(int)resp.StatusCode}");

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var items = new List<TraktWatchedItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var ids = el.GetProperty("movie").GetProperty("ids");
                items.Add(new TraktWatchedItem(
                    ids.TryGetProperty("imdb", out var imdb) ? imdb.GetString() : null,
                    ids.TryGetProperty("tmdb", out var tmdb) ? tmdb.GetInt32().ToString() : null,
                    false, null, null));
            }
            return (items, null);
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    private async Task<(List<TraktWatchedItem>? items, string? error)> GetWatchedShowsAsync(
        string clientId, string accessToken, CancellationToken ct)
    {
        try
        {
            var req  = BuildRequest(HttpMethod.Get, "/sync/watched/shows", clientId, accessToken);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return (null, $"Shows HTTP {(int)resp.StatusCode}");

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var items = new List<TraktWatchedItem>();
            foreach (var show in doc.RootElement.EnumerateArray())
            {
                var ids = show.GetProperty("show").GetProperty("ids");
                var imdbId = ids.TryGetProperty("imdb", out var imdb) ? imdb.GetString() : null;
                var tmdbId = ids.TryGetProperty("tmdb", out var tmdb) ? tmdb.GetInt32().ToString() : null;
                foreach (var season in show.GetProperty("seasons").EnumerateArray())
                {
                    var s = season.GetProperty("number").GetInt32();
                    foreach (var ep in season.GetProperty("episodes").EnumerateArray())
                    {
                        var e = ep.GetProperty("number").GetInt32();
                        items.Add(new TraktWatchedItem(imdbId, tmdbId, true, s, e));
                    }
                }
            }
            return (items, null);
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static HttpRequestMessage BuildRequest(
        HttpMethod method, string path,
        string clientId, string accessToken, string? jsonBody = null)
    {
        var req = new HttpRequestMessage(method, $"{BaseUrl}{path}");
        req.Headers.Add("trakt-api-key", clientId);
        req.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", SecureKeyService.Decrypt(accessToken));
        if (jsonBody != null)
            req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        return req;
    }

    private static object BuildIds(string? imdbId, string? tmdbId)
    {
        if (!string.IsNullOrWhiteSpace(imdbId) && int.TryParse(tmdbId, out var tmdbInt))
            return new { imdb = imdbId, tmdb = tmdbInt };
        if (!string.IsNullOrWhiteSpace(imdbId))
            return new { imdb = imdbId };
        if (int.TryParse(tmdbId, out var t))
            return new { tmdb = t };
        return new { };
    }
}
