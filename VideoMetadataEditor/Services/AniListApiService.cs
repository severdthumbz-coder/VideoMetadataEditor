using System.Net.Http;
using System.Text;
using Newtonsoft.Json.Linq;
using VideoMetadataEditor.Models;

namespace VideoMetadataEditor.Services;

/// <summary>
/// AniList GraphQL API client.
///
/// No API key required — AniList's public API allows anonymous read queries.
/// Rate limit: 90 requests per minute (shared across all anonymous callers from an IP).
/// All results are cached in the shared ApiService session cache (30 min TTL).
///
/// Use for anime films and OVAs where TMDB/OMDB coverage is weak or wrong.
/// The user enables AniList via Settings → Metadata Sources → "Include AniList".
/// AutoSearchAsync tries AniList ONLY when the TMDB+OMDB pipeline returns no result,
/// or when the user explicitly selects "AniList" as the preferred source for a file.
/// </summary>
public class AniListApiService
{
    private const string Endpoint = "https://graphql.anilist.co";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
        DefaultRequestHeaders = { { "Accept", "application/json" } }
    };

    // Shared with ApiService session cache
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string json, DateTime expiry)> _cache = new();

    // ─────────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Searches AniList for anime movies/OVAs matching <paramref name="title"/> and optional <paramref name="year"/>.
    /// Returns up to 5 results mapped to <see cref="SearchResult"/>.
    /// </summary>
    public async Task<(List<SearchResult> results, string? error)> SearchAsync(
        string title, string? year = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
            return ([], null);

        var cacheKey = $"anilist:search:{title.ToLowerInvariant()}:{year}";
        if (_cache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow < cached.expiry)
        {
            return (ParseSearchResults(cached.json), null);
        }

        var query = """
            query ($search: String, $year: Int) {
              Page(perPage: 5) {
                media(search: $search, seasonYear: $year, type: ANIME,
                      format_in: [MOVIE, OVA, SPECIAL]) {
                  id
                  title { romaji english native }
                  startDate { year }
                  genres
                  coverImage { large }
                  description(asHtml: false)
                  averageScore
                  staff(sort: RELEVANCE) {
                    edges { role node { name { full } } }
                  }
                  characters(sort: ROLE, perPage: 10) {
                    nodes { name { full } }
                  }
                }
              }
            }
            """;

        var variables = new JObject
        {
            ["search"] = title,
            ["year"]   = string.IsNullOrWhiteSpace(year) || !int.TryParse(year, out var y)
                         ? null : (JToken)y
        };

        try
        {
            var body    = new StringContent(
                new JObject { ["query"] = query, ["variables"] = variables }.ToString(),
                Encoding.UTF8, "application/json");
            var resp    = await _http.PostAsync(Endpoint, body, ct);
            var json    = await resp.Content.ReadAsStringAsync(ct);

            _cache[cacheKey] = (json, DateTime.UtcNow.Add(CacheTtl));
            return (ParseSearchResults(json), null);
        }
        catch (Exception ex) { return ([], ex.Message); }
    }

    /// <summary>
    /// Fetches full metadata for an AniList media ID.
    /// </summary>
    public async Task<(MovieMetadata? meta, string? error)> GetDetailsAsync(
        string aniListId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(aniListId))
            return (null, "No AniList ID provided");

        var cacheKey = $"anilist:detail:{aniListId}";
        if (_cache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow < cached.expiry)
            return (ParseDetails(cached.json), null);

        var query = """
            query ($id: Int) {
              Media(id: $id, type: ANIME) {
                id
                title { romaji english native }
                startDate { year }
                genres
                coverImage { extraLarge large }
                description(asHtml: false)
                averageScore
                staff(sort: RELEVANCE, perPage: 20) {
                  edges { role node { name { full } } }
                }
                characters(sort: ROLE, perPage: 10) {
                  nodes { name { full } }
                }
              }
            }
            """;

        var variables = new JObject { ["id"] = int.Parse(aniListId) };

        try
        {
            var body = new StringContent(
                new JObject { ["query"] = query, ["variables"] = variables }.ToString(),
                Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync(Endpoint, body, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);

            _cache[cacheKey] = (json, DateTime.UtcNow.Add(CacheTtl));
            var meta = ParseDetails(json);
            if (meta != null)
            {
                // Download poster art
                var artUrl = JObject.Parse(json)?["data"]?["Media"]?["coverImage"]?["extraLarge"]?.ToString()
                          ?? JObject.Parse(json)?["data"]?["Media"]?["coverImage"]?["large"]?.ToString();
                if (!string.IsNullOrWhiteSpace(artUrl))
                    meta.ArtworkBytes = await TryDownloadAsync(artUrl, ct);
            }
            return (meta, null);
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Parsers
    // ─────────────────────────────────────────────────────────────────────────

    private static List<SearchResult> ParseSearchResults(string json)
    {
        var results = new List<SearchResult>();
        try
        {
            var mediaList = JObject.Parse(json)?["data"]?["Page"]?["media"] as JArray;
            if (mediaList == null) return results;
            foreach (var item in mediaList)
            {
                var title = item["title"]?["english"]?.ToString()
                         ?? item["title"]?["romaji"]?.ToString()
                         ?? string.Empty;
                var year  = item["startDate"]?["year"]?.ToString() ?? string.Empty;
                var id    = item["id"]?.ToString() ?? string.Empty;
                var poster = item["coverImage"]?["large"]?.ToString() ?? string.Empty;
                results.Add(new SearchResult
                {
                    Title      = title,
                    Year       = year,
                    TmdbId     = string.Empty,
                    ImdbId     = string.Empty,
                    PosterUrl  = poster,
                    Overview   = CleanDescription(item["description"]?.ToString()),
                    // Stash AniList ID in a reserved field for later detail fetch
                    AniListId  = id,
                });
            }
        }
        catch { }
        return results;
    }

    private static MovieMetadata? ParseDetails(string json)
    {
        try
        {
            var media = JObject.Parse(json)?["data"]?["Media"];
            if (media == null) return null;

            var title = media["title"]?["english"]?.ToString()
                     ?? media["title"]?["romaji"]?.ToString()
                     ?? string.Empty;
            var year  = media["startDate"]?["year"]?.ToString() ?? string.Empty;

            var genres = (media["genres"] as JArray)?
                .Select(g => g.ToString())
                .Where(g => !string.IsNullOrWhiteSpace(g))
                .ToList() ?? new List<string>();

            // Extract director (role == "Director")
            var director = (media["staff"]?["edges"] as JArray)?
                .FirstOrDefault(e => e["role"]?.ToString()
                    .Contains("Director", StringComparison.OrdinalIgnoreCase) == true)
                ?["node"]?["name"]?["full"]?.ToString() ?? string.Empty;

            // Extract cast (character names — AniList uses character names not VA names)
            var cast = string.Join(", ",
                ((media["characters"]?["nodes"] as JArray) ?? new JArray())
                .Select(c => c["name"]?["full"]?.ToString() ?? string.Empty)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Take(10));

            // AniList scores are 0-100; convert to 0-10
            float rating = 0f;
            if (float.TryParse(media["averageScore"]?.ToString(), out var raw100))
                rating = raw100 / 10f;

            return new MovieMetadata
            {
                Title       = title,
                Year        = year,
                Genre       = string.Join(", ", genres),
                Director    = director,
                Cast        = cast,
                Description = CleanDescription(media["description"]?.ToString()),
                Rating      = MathF.Round(rating, 1),
                MpaRating   = "NR",          // AniList has no MPA cert
                PosterUrl   = media["coverImage"]?["large"]?.ToString() ?? string.Empty,
                // AniList ID stored so SyncLibraryEntry and cache can reference it
                TmdbId      = string.Empty,
                ImdbId      = string.Empty,
                AniListId   = media["id"]?.ToString() ?? string.Empty,
            };
        }
        catch { return null; }
    }

    /// <summary>
    /// Computes the ABSOLUTE (series-wide) episode number for a given AniList entry and
    /// in-season episode, by summing the episode counts of all PREQUEL-chained earlier
    /// entries and adding the in-season number.
    ///
    /// This is the FALLBACK path for {AbsoluteEpisode}, used only when the source
    /// filename carries no absolute number. It is inherently best-effort:
    ///   • AniList splits each anime "season" into a separate Media entry; the PREQUEL
    ///     relation chain is how seasons connect, but specials/OVAs/recaps can sit on
    ///     the chain and skew the sum.
    ///   • AniList's season boundaries often do NOT match TMDB/Plex season numbering.
    /// On any uncertainty (missing episode counts, a branching relation graph, network
    /// failure) it returns null so the caller leaves {AbsoluteEpisode} empty rather than
    /// emitting a confidently-wrong number. Never throws.
    /// </summary>
    public async Task<int?> ComputeAbsoluteEpisodeAsync(
        string aniListId, int inSeasonEpisode, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(aniListId) || inSeasonEpisode <= 0) return null;
        if (!int.TryParse(aniListId, out int startId)) return null;

        var cacheKey = $"anilist:absbase:{startId}";
        int? priorSum = null;

        if (_cache.TryGetValue(cacheKey, out var cached) && DateTime.UtcNow < cached.expiry
            && int.TryParse(cached.json, out int cachedBase))
        {
            priorSum = cachedBase;
        }
        else
        {
            priorSum = await WalkPrequelEpisodeSumAsync(startId, ct);
            if (priorSum.HasValue)
                _cache[cacheKey] = (priorSum.Value.ToString(), DateTime.UtcNow.Add(CacheTtl));
        }

        if (!priorSum.HasValue) return null;
        return priorSum.Value + inSeasonEpisode;
    }

    /// <summary>
    /// Walks the PREQUEL relation chain backwards from <paramref name="startId"/>,
    /// summing each earlier entry's episode count. Returns the total number of episodes
    /// that aired BEFORE the start entry, or null if the chain can't be resolved
    /// unambiguously (missing counts, a fork in the chain, a cycle, or a network error).
    /// </summary>
    private static async Task<int?> WalkPrequelEpisodeSumAsync(int startId, CancellationToken ct)
    {
        const int MaxHops = 20; // guard against cycles / pathological chains
        int sum = 0;
        int currentId = startId;
        var visited = new HashSet<int> { startId };

        try
        {
            for (int hop = 0; hop < MaxHops; hop++)
            {
                var query = """
                    query ($id: Int) {
                      Media(id: $id, type: ANIME) {
                        id
                        episodes
                        relations {
                          edges { relationType node { id type episodes } }
                        }
                      }
                    }
                    """;
                var variables = new JObject { ["id"] = currentId };
                var body = new StringContent(
                    new JObject { ["query"] = query, ["variables"] = variables }.ToString(),
                    Encoding.UTF8, "application/json");
                var resp = await _http.PostAsync(Endpoint, body, ct);
                var json = await resp.Content.ReadAsStringAsync(ct);

                var media = JObject.Parse(json)?["data"]?["Media"];
                if (media == null) return null;

                // Find the single ANIME prequel, if any.
                var edges = media["relations"]?["edges"] as JArray ?? new JArray();
                var prequels = edges
                    .Where(e => string.Equals(e["relationType"]?.ToString(), "PREQUEL",
                                StringComparison.OrdinalIgnoreCase)
                             && string.Equals(e["node"]?["type"]?.ToString(), "ANIME",
                                StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (prequels.Count == 0) return sum;       // reached the first season
                if (prequels.Count > 1) return null;        // ambiguous fork — bail

                var prequel = prequels[0]["node"];
                if (prequel == null) return null;
                if (!int.TryParse(prequel["id"]?.ToString(), out int prequelId)) return null;
                if (!visited.Add(prequelId)) return null;    // cycle — bail

                // A prequel with an unknown/zero episode count makes the sum unreliable.
                if (!int.TryParse(prequel["episodes"]?.ToString(), out int prequelEps)
                    || prequelEps <= 0)
                    return null;

                sum += prequelEps;
                currentId = prequelId;
            }
            return null; // exceeded MaxHops without terminating — treat as unresolved
        }
        catch { return null; }
    }

    private static string CleanDescription(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        // AniList descriptions sometimes contain HTML-like tags even with asHtml:false
        return System.Text.RegularExpressions.Regex
            .Replace(raw, @"<[^>]+>", string.Empty)
            .Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">")
            .Replace("&#39;", "'").Replace("&quot;", "\"")
            .Trim();
    }

    private static async Task<byte[]?> TryDownloadAsync(string url, CancellationToken ct)
    {
        try { return await _http.GetByteArrayAsync(url, ct); }
        catch { return null; }
    }
}
