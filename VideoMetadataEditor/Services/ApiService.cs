using System.Collections.Concurrent;
using System.Net.Http;
using Newtonsoft.Json.Linq;
using VideoMetadataEditor.Models;

namespace VideoMetadataEditor.Services;

// Language tag injected by the ViewModel after settings load.
// Defaults to "en-US" — replaced on startup from AppSettings.MetadataLanguage.


public class ApiService
{
    /// <summary>
    /// IETF language tag used for all TMDB API requests.
    /// Set from AppSettings.MetadataLanguage on startup and when changed.
    /// Example values: "en-US", "ko-KR", "ja-JP", "fr-FR", "de-DE".
    /// </summary>
    public static string MetadataLanguage { get; set; } = "en-US";

    // ── Static HttpClient — shared to avoid socket exhaustion ────────────────
    // HttpClient is thread-safe and designed to be reused for the lifetime of the app.
    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    static ApiService()
    {
        _http.DefaultRequestHeaders.Add("User-Agent", "VideoMetadataEditor/1.3.4");
    }

    // ── Search result cache — keyed by "source:query" or "source:id" ─────────
    // Each entry has a 30-minute TTL. Stale entries are evicted lazily on read
    // and proactively when the cache exceeds MaxCacheEntries.
    private record CacheEntry(object? Value, DateTime ExpiresAt);
    private static readonly ConcurrentDictionary<string, CacheEntry> _cache = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);

    private static T? GetCached<T>(string key) where T : class
    {
        if (!_cache.TryGetValue(key, out var entry)) return null;
        if (DateTime.UtcNow > entry.ExpiresAt)
        {
            _cache.TryRemove(key, out _); // stale — evict lazily
            return null;
        }
        return entry.Value as T;
    }

    private const int MaxCacheEntries = 500;
    private static void SetCached(string key, object? value)
    {
        if (_cache.Count >= MaxCacheEntries)
        {
            // Evict expired entries first; fall back to oldest 50 if none expired
            var now     = DateTime.UtcNow;
            var expired = _cache.Where(kv => now > kv.Value.ExpiresAt).Select(kv => kv.Key).ToList();
            if (expired.Count > 0)
                foreach (var k in expired) _cache.TryRemove(k, out _);
            else
                foreach (var k in _cache.Keys.Take(50).ToList()) _cache.TryRemove(k, out _);
        }
        _cache[key] = new CacheEntry(value, DateTime.UtcNow.Add(CacheTtl));
    }

    // ── Resilient HTTP get with retry ─────────────────────────────────────────
    // Retries on transient failures (network, 5xx, 429) with exponential back-off.
    // Returns (json, errorMessage) — errorMessage is null on success.
    private static async Task<(string? json, string? error)> GetWithRetryAsync(
        string url, CancellationToken ct = default, int maxAttempts = 3)
    {
        int delay = 600;
        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);

                // Rate-limit: back off longer
                if (resp.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    int wait = (int)(resp.Headers.RetryAfter?.Delta?.TotalMilliseconds ?? delay * 3);
                    if (attempt < maxAttempts) await Task.Delay(wait, ct);
                    continue;
                }

                if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                    return (null, "API key is invalid or expired.");

                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return (null, "Not found.");

                if (!resp.IsSuccessStatusCode)
                {
                    if (attempt < maxAttempts) { await Task.Delay(delay, ct); delay *= 2; }
                    continue;
                }

                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return (body, null);
            }
            catch (OperationCanceledException) { return (null, "Request cancelled or timed out."); }
            catch (HttpRequestException ex)
            {
                if (attempt < maxAttempts) { await Task.Delay(delay, ct); delay *= 2; }
                else return (null, $"Network error: {ex.Message}");
            }
        }
        return (null, "Request failed after retries.");
    }

    // ── TMDB ──────────────────────────────────────────────────────────────────

    public async Task<(List<SearchResult> results, string? error)> SearchTmdbAsync(
        string query, string apiKey, string? year = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return ([], "TMDB API key is not set. Add it in the SETTINGS tab.");

        var cacheKey = $"tmdb:search:{query.ToLowerInvariant()}:{year ?? ""}";
        var cached   = GetCached<List<SearchResult>>(cacheKey);
        if (cached != null) return (cached, null);

        var yearParam = !string.IsNullOrWhiteSpace(year) ? $"&year={Uri.EscapeDataString(year)}" : string.Empty;

        var url = $"https://api.themoviedb.org/3/search/movie?api_key={apiKey}" +
                  $"&query={Uri.EscapeDataString(query)}&language={MetadataLanguage}&page=1{yearParam}";

        var (json, error) = await GetWithRetryAsync(url, ct);
        if (json == null) return ([], error ?? "TMDB search failed.");

        try
        {
            var obj     = JObject.Parse(json);
            var results = new List<SearchResult>();
            foreach (var item in obj["results"] ?? new JArray())
            {
                results.Add(new SearchResult
                {
                    Title      = item["title"]?.ToString() ?? "",
                    Year       = item["release_date"]?.ToString().Split('-').FirstOrDefault() ?? "",
                    TmdbId     = item["id"]?.ToString() ?? "",
                    Popularity = item["popularity"] != null
                        ? double.TryParse(item["popularity"]!.ToString(),
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double pop)
                                ? pop : 0 : 0,
                    PosterUrl  = item["poster_path"] != null
                        ? $"https://image.tmdb.org/t/p/w185{item["poster_path"]}"
                        : "",
                    Overview   = item["overview"]?.ToString() ?? ""
                });
            }
            SetCached(cacheKey, results);
            return (results, null);
        }
        catch (Exception ex) { return ([], $"Failed to parse TMDB response: {ex.Message}"); }
    }

    public async Task<(MovieMetadata? meta, string? error)> GetTmdbDetailsAsync(
        string tmdbId, string apiKey, CancellationToken ct = default,
        bool downloadArtwork = true)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return (null, "TMDB API key is not set.");
        if (string.IsNullOrWhiteSpace(tmdbId))
            return (null, "No TMDB ID provided.");

        var cacheKey = $"tmdb:detail:{tmdbId}";
        var cached   = GetCached<MovieMetadata>(cacheKey);
        if (cached != null) return (cached, null);

        var url = $"https://api.themoviedb.org/3/movie/{tmdbId}" +
                  $"?api_key={apiKey}&append_to_response=credits,release_dates&language={MetadataLanguage}";

        var (json, error) = await GetWithRetryAsync(url, ct);
        if (json == null) return (null, error);

        try
        {
            var obj  = JObject.Parse(json);
            var meta = new MovieMetadata
            {
                Title       = obj["title"]?.ToString() ?? "",
                Year        = obj["release_date"]?.ToString().Split('-').FirstOrDefault() ?? "",
                Description = obj["overview"]?.ToString() ?? "",
                Genre       = string.Join(", ",
                                  (obj["genres"] as JArray ?? new JArray())
                                  .Select(g => g["name"]?.ToString() ?? "")
                                  .Where(g => !string.IsNullOrWhiteSpace(g))
                                  .OrderBy(g => g)),
                TmdbId      = tmdbId,
                ImdbId      = obj["imdb_id"]?.ToString() ?? "",
                PosterUrl   = obj["poster_path"] != null
                    ? $"https://image.tmdb.org/t/p/w500{obj["poster_path"]}"
                    : "",
                TmdbUrl     = $"https://www.themoviedb.org/movie/{tmdbId}",
                ImdbUrl     = !string.IsNullOrEmpty(obj["imdb_id"]?.ToString())
                    ? $"https://www.imdb.com/title/{obj["imdb_id"]}" : null
            };

            if (float.TryParse(obj["vote_average"]?.ToString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float r) && r > 0)
            {
                meta.Rating = r;
                if (int.TryParse(obj["vote_count"]?.ToString(), out int v)) meta.RatingVotes = v;
            }

            var credits = obj["credits"];
            if (credits != null)
            {
                meta.Director = string.Join(", ", credits["crew"]?
                    .Where(c => c["job"]?.ToString() == "Director")
                    .Select(c => c["name"]?.ToString() ?? "") ?? []);
                meta.Cast = string.Join(", ", credits["cast"]?
                    .Select(c => c["name"]?.ToString() ?? "")
                    .Where(n => !string.IsNullOrWhiteSpace(n)) ?? []);
            }

            // Extract US MPA certification from release_dates
            var relDates = obj["release_dates"]?["results"] as Newtonsoft.Json.Linq.JArray;
            if (relDates != null)
            {
                var us = relDates.FirstOrDefault(r => r["iso_3166_1"]?.ToString() == "US");
                var cert = us?["release_dates"]
                    ?.Select(d => d["certification"]?.ToString() ?? "")
                    .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
                if (!string.IsNullOrWhiteSpace(cert))
                    meta.MpaRating = cert;
            }

            // Only download artwork when explicitly requested.
            // During batch search (calledFromBatch=true path in AutoSearchAsync) artwork
            // is skipped to avoid 100+ simultaneous image downloads. The user can fetch
            // artwork later via Bulk Artwork Download or by selecting individual files.
            if (downloadArtwork && !string.IsNullOrWhiteSpace(meta.PosterUrl))
            {
                try { meta.ArtworkBytes = await _http.GetByteArrayAsync(meta.PosterUrl, ct); }
                catch { /* artwork optional */ }
            }

            SetCached(cacheKey, meta);
            return (meta, null);
        }
        catch (Exception ex) { return (null, $"Failed to parse TMDB details: {ex.Message}"); }
    }

    public async Task<(MovieMetadata? meta, string? error)> GetTmdbByImdbIdAsync(
        string imdbId, string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return (null, "TMDB API key is not set.");

        var url = $"https://api.themoviedb.org/3/find/{imdbId}" +
                  $"?api_key={apiKey}&external_source=imdb_id";
        var (json, error) = await GetWithRetryAsync(url, ct);
        if (json == null) return (null, error);

        try
        {
            var obj     = JObject.Parse(json);
            var results = obj["movie_results"] as JArray;
            if (results == null || results.Count == 0) return (null, "No TMDB match for that IMDB ID.");
            var tmdbId = results[0]["id"]?.ToString() ?? "";
            return await GetTmdbDetailsAsync(tmdbId, apiKey, ct);
        }
        catch (Exception ex) { return (null, $"TMDB find-by-IMDB error: {ex.Message}"); }
    }

    // ── OMDB ──────────────────────────────────────────────────────────────────

    public async Task<(List<SearchResult> results, string? error)> SearchOmdbAsync(
        string query, string apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
            return ([], "OMDB API key is not set. Add it in the SETTINGS tab.");

        var cacheKey = $"omdb:search:{query.ToLowerInvariant()}";
        var cached   = GetCached<List<SearchResult>>(cacheKey);
        if (cached != null) return (cached, null);

        var url = $"https://www.omdbapi.com/?apikey={apiKey}" +
                  $"&s={Uri.EscapeDataString(query)}&type=movie";

        var (json, error) = await GetWithRetryAsync(url, ct);
        if (json == null) return ([], error ?? "OMDB search failed.");

        try
        {
            var obj = JObject.Parse(json);
            if (obj["Response"]?.ToString() != "True")
            {
                var reason = obj["Error"]?.ToString() ?? "No results found.";
                return ([], reason);
            }

            var results = new List<SearchResult>();
            foreach (var item in obj["Search"] ?? new JArray())
            {
                results.Add(new SearchResult
                {
                    Title     = item["Title"]?.ToString() ?? "",
                    Year      = item["Year"]?.ToString() ?? "",
                    ImdbId    = item["imdbID"]?.ToString() ?? "",
                    PosterUrl = item["Poster"]?.ToString() ?? ""
                });
            }
            SetCached(cacheKey, results);
            return (results, null);
        }
        catch (Exception ex) { return ([], $"Failed to parse OMDB response: {ex.Message}"); }
    }

    public async Task<(MovieMetadata? meta, string? error)> GetOmdbDetailsAsync(
        string imdbId, string apiKey, CancellationToken ct = default,
        bool downloadArtwork = true)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return (null, "OMDB API key is not set.");

        var cacheKey = $"omdb:detail:{imdbId}";
        var cached   = GetCached<MovieMetadata>(cacheKey);
        if (cached != null) return (cached, null);

        var url = $"https://www.omdbapi.com/?i={imdbId}&apikey={apiKey}&plot=full";
        var (json, error) = await GetWithRetryAsync(url, ct);
        if (json == null) return (null, error);

        try
        {
            var obj = JObject.Parse(json);
            if (obj["Response"]?.ToString() != "True")
                return (null, obj["Error"]?.ToString() ?? "OMDB returned an error.");

            var meta = new MovieMetadata
            {
                Title       = obj["Title"]?.ToString() ?? "",
                Year        = obj["Year"]?.ToString() ?? "",
                Description = obj["Plot"]?.ToString() ?? "",
                Genre       = string.Join(", ",
                                  (obj["Genre"]?.ToString() ?? "")
                                  .Split(',', StringSplitOptions.TrimEntries)
                                  .Where(g => !string.IsNullOrWhiteSpace(g))
                                  .OrderBy(g => g)),
                Director    = obj["Director"]?.ToString() ?? "",
                Cast        = obj["Actors"]?.ToString() ?? "",
                MpaRating   = NormaliseRating(obj["Rated"]?.ToString()),
                ImdbId      = imdbId,
                ImdbUrl     = $"https://www.imdb.com/title/{imdbId}",
                OmdbUrl     = $"https://www.omdbapi.com/?i={imdbId}",
                PosterUrl   = obj["Poster"]?.ToString() ?? ""
            };

            if (float.TryParse(obj["imdbRating"]?.ToString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float r) && r > 0)
            {
                meta.Rating = r;
                var votesStr = (obj["imdbVotes"]?.ToString() ?? "").Replace(",", "");
                if (int.TryParse(votesStr, out int v)) meta.RatingVotes = v;
            }

            if (downloadArtwork && !string.IsNullOrWhiteSpace(meta.PosterUrl) && meta.PosterUrl != "N/A")
            {
                try { meta.ArtworkBytes = await _http.GetByteArrayAsync(meta.PosterUrl, ct); }
                catch { }
            }

            SetCached(cacheKey, meta);
            return (meta, null);
        }
        catch (Exception ex) { return (null, $"Failed to parse OMDB details: {ex.Message}"); }
    }

    // ── API Key Validation ────────────────────────────────────────────────────

    public async Task<bool> ValidateTmdbKeyAsync(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return false;
        var (_, error) = await GetWithRetryAsync(
            $"https://api.themoviedb.org/3/configuration?api_key={apiKey}", maxAttempts: 1);
        return error == null;
    }

    public async Task<bool> ValidateOmdbKeyAsync(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return false;
        var (json, _) = await GetWithRetryAsync(
            $"https://www.omdbapi.com/?i=tt0111161&apikey={apiKey}", maxAttempts: 1);
        return json?.Contains("\"Response\":\"True\"") == true;
    }

    /// <summary>Clears the in-memory search/detail cache (e.g. after an API key change).</summary>
    public static void ClearCache() => _cache.Clear();
    // ── TV / Episode API ──────────────────────────────────────────────────────

    /// <summary>Searches TMDB for TV series matching <paramref name="query"/>.</summary>
    public async Task<(List<SearchResult> results, string? error)> SearchTvAsync(
        string query, string apiKey, CancellationToken ct = default,
        string? firstAirYear = null)
    {
        var key = SecureKeyService.Decrypt(apiKey?.Trim() ?? "");
        if (string.IsNullOrWhiteSpace(key))
            return ([], "TMDB API key is not set. Add it in the SETTINGS tab.");

        var cacheKey = $"tmdb:tv:search:{query.ToLowerInvariant()}:{firstAirYear ?? ""}";
        var cached   = GetCached<List<SearchResult>>(cacheKey);
        if (cached != null) return (cached, null);

        // NOTE: Do NOT pass first_air_date_year for episode files.
        // The year in a torrent filename is the encode year, not the show's premiere year.
        // "Dr.Stone.S04E01.2024..." should search "Dr Stone" with NO year filter.
        // Year filtering is only useful when searching for a show by its premiere year directly.
        var yearParam = !string.IsNullOrWhiteSpace(firstAirYear)
            ? $"&first_air_date_year={Uri.EscapeDataString(firstAirYear)}" : string.Empty;

        var url = $"https://api.themoviedb.org/3/search/tv" +
                  $"?api_key={key}&query={Uri.EscapeDataString(query)}&language={MetadataLanguage}{yearParam}";
        var (json, error) = await GetWithRetryAsync(url, ct);
        if (json == null) return ([], error);

        try
        {
            var results = new List<SearchResult>();
            var arr = JObject.Parse(json)?["results"] as JArray ?? [];
            foreach (var item in arr.Take(8))
            {
                var title    = item["name"]?.ToString() ?? item["original_name"]?.ToString() ?? "";
                var firstAir = item["first_air_date"]?.ToString() ?? "";
                var year     = firstAir.Length >= 4 ? firstAir[..4] : "";
                var tmdbId   = item["id"]?.ToString() ?? "";
                results.Add(new SearchResult
                {
                    Title     = title,
                    // Year is the premiere year — blank for ongoing shows; show "TV" to distinguish
                    Year      = !string.IsNullOrWhiteSpace(year) ? year : "TV",
                    TmdbId    = tmdbId,
                    Overview  = item["overview"]?.ToString() ?? "",
                    PosterUrl = item["poster_path"]?.ToString() is string p && !string.IsNullOrEmpty(p)
                                  ? $"https://image.tmdb.org/t/p/w300{p}" : "",
                    Source    = "TMDB_TV",
                });
            }
            SetCached(cacheKey, results);
            return (results, null);
        }
        catch (Exception ex) { return ([], ex.Message); }
    }

    /// <summary>
    /// Fetches full metadata for a specific TV episode.
    /// <paramref name="seriesTmdbId"/> is the series ID (from SearchTvAsync).
    /// </summary>
    public async Task<(MovieMetadata? meta, string? error)> GetTvEpisodeAsync(
        string seriesTmdbId, int season, int episode,
        string apiKey, CancellationToken ct = default)
    {
        var key = SecureKeyService.Decrypt(apiKey?.Trim() ?? "");
        if (string.IsNullOrWhiteSpace(key)) return (null, "TMDB API key not set.");
        if (string.IsNullOrWhiteSpace(seriesTmdbId)) return (null, "No series ID.");

        // ── Series-level metadata (show title, genres, network) ───────────────
        var seriesCacheKey = $"tmdb:tv:series:{seriesTmdbId}";
        JObject? seriesObj = null;
        var seriesCached   = GetCached<string>(seriesCacheKey);
        if (seriesCached != null)
        {
            try { seriesObj = JObject.Parse(seriesCached); } catch { }
        }
        else
        {
            var seriesUrl = $"https://api.themoviedb.org/3/tv/{seriesTmdbId}" +
                            $"?api_key={key}&append_to_response=content_ratings,external_ids&language={MetadataLanguage}";
            var (sJson, _) = await GetWithRetryAsync(seriesUrl, ct);
            if (sJson != null) { seriesObj = JObject.Parse(sJson); SetCached(seriesCacheKey, sJson); }
        }

        // ── Episode-level metadata ────────────────────────────────────────────
        var epCacheKey = $"tmdb:tv:ep:{seriesTmdbId}:{season}:{episode}";
        var epCached   = GetCached<string>(epCacheKey);
        JObject? epObj = null;
        if (epCached != null)
        {
            try { epObj = JObject.Parse(epCached); } catch { }
        }
        else
        {
            var epUrl = $"https://api.themoviedb.org/3/tv/{seriesTmdbId}" +
                        $"/season/{season}/episode/{episode}" +
                        $"?api_key={key}&append_to_response=credits&language={MetadataLanguage}";
            var (eJson, eErr) = await GetWithRetryAsync(epUrl, ct);
            if (eJson == null) return (null, eErr);
            epObj = JObject.Parse(eJson);
            SetCached(epCacheKey, eJson);
        }
        if (epObj == null) return (null, "Episode data not found.");

        try
        {
            var showTitle  = seriesObj?["name"]?.ToString() ?? "";
            var genres     = string.Join(", ", (seriesObj?["genres"] as JArray ?? [])
                               .Select(g => g["name"]?.ToString() ?? "")
                               .Where(g => !string.IsNullOrEmpty(g)));

            // MPA from content_ratings
            var mpa = (seriesObj?["content_ratings"]?["results"] as JArray ?? [])
                        .FirstOrDefault(r => r["iso_3166_1"]?.ToString() == "US")
                        ?["rating"]?.ToString() ?? "";
            if (string.IsNullOrEmpty(mpa)) mpa = "NR";

            // Director from episode crew
            var director = (epObj["credits"]?["crew"] as JArray ?? [])
                             .FirstOrDefault(c => c["job"]?.ToString() == "Director")
                             ?["name"]?.ToString() ?? "";

            // Cast — episode guest stars + series regulars
            var guestStars = (epObj["credits"]?["guest_stars"] as JArray ?? [])
                               .Select(c => c["name"]?.ToString() ?? "")
                               .Where(n => !string.IsNullOrEmpty(n));
            var mainCast   = (epObj["credits"]?["cast"] as JArray ?? [])
                               .Select(c => c["name"]?.ToString() ?? "")
                               .Where(n => !string.IsNullOrEmpty(n));
            var cast       = string.Join(", ", mainCast.Take(6).Concat(guestStars.Take(4)));

            float rating = 0f;
            if (float.TryParse(epObj["vote_average"]?.ToString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var r))
                rating = MathF.Round(r, 1);

            var airDate = epObj["air_date"]?.ToString() ?? "";

            var meta = new MovieMetadata
            {
                Title         = epObj["name"]?.ToString() ?? "",
                Year          = airDate.Length >= 4 ? airDate[..4] : "",
                Description   = epObj["overview"]?.ToString() ?? "",
                Genre         = genres,
                Director      = director,
                Cast          = cast,
                Rating        = rating,
                MpaRating     = mpa,
                // TV-specific fields
                IsEpisode     = true,
                ShowTitle     = showTitle,
                Season        = season,
                Episode       = episode,
                EpisodeTitle  = epObj["name"]?.ToString() ?? "",
                AiredDate     = airDate,
                TmdbId        = epObj["id"]?.ToString() ?? "",
                TmdbSeriesId  = seriesTmdbId,
                ImdbId        = seriesObj?["external_ids"]?["imdb_id"]?.ToString() ?? "",
                // Poster: prefer series poster (vertical format, good for file cover art)
                // Fall back to episode still if no series poster available
                PosterUrl     = (seriesObj?["poster_path"]?.ToString() is string pp && !string.IsNullOrEmpty(pp))
                                  ? $"https://image.tmdb.org/t/p/w500{pp}"
                                  : (epObj["still_path"]?.ToString() is string sp && !string.IsNullOrEmpty(sp))
                                    ? $"https://image.tmdb.org/t/p/w500{sp}" : "",
                TmdbUrl       = $"https://www.themoviedb.org/tv/{seriesTmdbId}/season/{season}/episode/{episode}",
            };

            // Download poster art
            if (!string.IsNullOrEmpty(meta.PosterUrl))
            {
                try { meta.ArtworkBytes = await _http.GetByteArrayAsync(meta.PosterUrl, ct); } catch { }
            }

            return (meta, null);
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    
    /// <summary>
    /// Fetches the number of seasons and basic series info for a TV show.
    /// Returns (seasonCount, showTitle, error).
    /// </summary>
    public async Task<(int seasonCount, string showTitle, string? error)> GetTvSeriesInfoAsync(
        string seriesTmdbId, string apiKey, CancellationToken ct = default)
    {
        var key = SecureKeyService.Decrypt(apiKey?.Trim() ?? "");
        if (string.IsNullOrWhiteSpace(key)) return (0, "", "TMDB API key not set.");

        var cacheKey = $"tmdb:tv:info:{seriesTmdbId}";
        var cached   = GetCached<string>(cacheKey);
        JObject? obj;
        if (cached != null) { obj = JObject.Parse(cached); }
        else
        {
            var url = $"https://api.themoviedb.org/3/tv/{seriesTmdbId}?api_key={key}&language={MetadataLanguage}";
            var (json, err) = await GetWithRetryAsync(url, ct);
            if (json == null) return (0, "", err);
            obj = JObject.Parse(json);
            SetCached(cacheKey, json);
        }
        var count = obj?["number_of_seasons"]?.Value<int>() ?? 0;
        var title = obj?["name"]?.ToString() ?? "";
        return (count, title, null);
    }

    /// <summary>
    /// Fetches the episode list for a specific season.
    /// Returns a list of (episodeNumber, episodeTitle, airDate) tuples.
    /// </summary>
    public async Task<(List<TvEpisodeItem> episodes, string? error)> GetTvSeasonEpisodesAsync(
        string seriesTmdbId, int season, string apiKey, CancellationToken ct = default)
    {
        var key = SecureKeyService.Decrypt(apiKey?.Trim() ?? "");
        if (string.IsNullOrWhiteSpace(key)) return ([], "TMDB API key not set.");

        var cacheKey = $"tmdb:tv:season:{seriesTmdbId}:{season}";
        var cached   = GetCached<string>(cacheKey);
        JObject? obj;
        if (cached != null) { obj = JObject.Parse(cached); }
        else
        {
            var url = $"https://api.themoviedb.org/3/tv/{seriesTmdbId}/season/{season}" +
                      $"?api_key={key}&language={MetadataLanguage}";
            var (json, err) = await GetWithRetryAsync(url, ct);
            if (json == null) return ([], err);
            obj = JObject.Parse(json);
            SetCached(cacheKey, json);
        }
        var episodes = (obj?["episodes"] as JArray ?? [])
            .Select(e => new TvEpisodeItem
            {
                EpisodeNumber = e["episode_number"]?.Value<int>() ?? 0,
                Title         = e["name"]?.ToString() ?? "",
                AirDate       = e["air_date"]?.ToString() ?? "",
                Overview      = e["overview"]?.ToString() ?? "",
            })
            .Where(e => e.EpisodeNumber > 0)
            .OrderBy(e => e.EpisodeNumber)
            .ToList();
        return (episodes, null);
    }

    /// <summary>
    /// Fetches TV episode data from OMDB using series title + season + episode.
    /// Used as a fallback when TMDB TV search fails.
    /// OMDB endpoint: ?t={title}&type=series&Season={s}&Episode={e}
    /// </summary>
    public async Task<(MovieMetadata? meta, string? error)> GetOmdbEpisodeAsync(
        string showTitle, int season, int episode,
        string apiKey, CancellationToken ct = default)
    {
        var key = SecureKeyService.Decrypt(apiKey?.Trim() ?? "");
        if (string.IsNullOrWhiteSpace(key)) return (null, "OMDB API key not set.");
        if (string.IsNullOrWhiteSpace(showTitle)) return (null, "No show title.");

        // Step 1: find the series IMDB ID
        var seriesCacheKey = $"omdb:tv:series:{showTitle.ToLowerInvariant()}";
        string? seriesImdbId = GetCached<string>(seriesCacheKey);
        if (string.IsNullOrEmpty(seriesImdbId))
        {
            var sUrl   = $"https://www.omdbapi.com/?t={Uri.EscapeDataString(showTitle)}&type=series&apikey={key}";
            var (sJson, sErr) = await GetWithRetryAsync(sUrl, ct);
            if (sJson == null) return (null, sErr);
            var sObj = JObject.Parse(sJson);
            if (sObj["Response"]?.ToString() != "True")
                return (null, sObj["Error"]?.ToString() ?? "Show not found on OMDB.");
            seriesImdbId = sObj["imdbID"]?.ToString() ?? "";
            if (!string.IsNullOrEmpty(seriesImdbId))
                SetCached(seriesCacheKey, seriesImdbId);
        }

        // Step 2: fetch the episode
        var epCacheKey = $"omdb:tv:ep:{seriesImdbId}:{season}:{episode}";
        var epCached   = GetCached<MovieMetadata>(epCacheKey);
        if (epCached != null) return (epCached, null);

        var epUrl = $"https://www.omdbapi.com/?i={seriesImdbId}&Season={season}&Episode={episode}&apikey={key}&plot=full";
        var (eJson, eErr2) = await GetWithRetryAsync(epUrl, ct);
        if (eJson == null) return (null, eErr2);

        var eObj = JObject.Parse(eJson);
        if (eObj["Response"]?.ToString() != "True")
            return (null, eObj["Error"]?.ToString() ?? "Episode not found on OMDB.");

        var meta = new MovieMetadata
        {
            Title         = eObj["Title"]?.ToString() ?? "",
            Year          = eObj["Year"]?.ToString() ?? "",
            Description   = eObj["Plot"]?.ToString() ?? "",
            Director      = eObj["Director"]?.ToString() ?? "",
            Cast          = eObj["Actors"]?.ToString() ?? "",
            MpaRating     = NormaliseRating(eObj["Rated"]?.ToString()),
            ImdbId        = eObj["imdbID"]?.ToString() ?? seriesImdbId,
            IsEpisode     = true,
            ShowTitle     = showTitle,
            Season        = season,
            Episode       = episode,
            EpisodeTitle  = eObj["Title"]?.ToString() ?? "",
            AiredDate     = eObj["Released"]?.ToString() ?? "",
        };
        if (float.TryParse(eObj["imdbRating"]?.ToString(),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float r) && r > 0)
            meta.Rating = r;

        SetCached(epCacheKey, meta);
        return (meta, null);
    }

    private static string NormaliseRating(string? raw) =>
        raw?.Trim() switch
        {
            null or ""   => string.Empty,
            "N/A"        => string.Empty,
            "Not Rated"  => "NR",
            "Unrated"    => "NR",
            "UNRATED"    => "NR",
            var r        => r
        };
}
