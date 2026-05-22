using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using VideoMetadataEditor.Models;

namespace VideoMetadataEditor.Services;

/// <summary>
/// OpenSubtitles REST API v1 client.
///
/// API base: https://api.opensubtitles.com/api/v1
/// Free tier: 5 downloads/day, 100 searches/day per API key.
/// Registration: opensubtitles.com (free account + generate API key in profile).
///
/// Auth: Api-Key header for search, Authorization: Bearer {token} for download.
/// The free tier does NOT require OAuth — Api-Key alone works for search and download.
/// </summary>
public class OpenSubtitlesService
{
    private const string Base = "https://api.opensubtitles.com/api/v1";

    private readonly HttpClient _http;

    public OpenSubtitlesService()
    {
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "VideoMetadataEditor/1.4 (contact@videometadataeditor.app)");
        _http.DefaultRequestHeaders.Accept
             .Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    // ── File hash (OpenSubtitles algorithm) ──────────────────────────────────

    /// <summary>
    /// Computes the OpenSubtitles file hash.
    /// Algorithm: XOR of every 8-byte chunk in the first and last 64KB of the file,
    /// then XOR with the file size (as uint64). Returns hex string.
    /// This is far more accurate than title-based search for exact subtitle matching.
    /// </summary>
    public static (string? hash, long fileSize) ComputeHash(string filePath)
    {
        const long chunkSize = 65536L; // 64KB
        try
        {
            var fileInfo = new FileInfo(filePath);
            var fileSize = fileInfo.Length;
            if (fileSize < chunkSize * 2) return (null, fileSize); // file too small

            ulong hash = (ulong)fileSize;

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite, 65536, FileOptions.SequentialScan);

            // Single allocation outside both loops — avoids 16,384 heap allocs per call
            var buf = new byte[8];

            // First 64KB
            fs.Seek(0, SeekOrigin.Begin);
            for (long i = 0; i < chunkSize / 8; i++)
            {
                if (fs.Read(buf, 0, 8) != 8) break;
                hash += BitConverter.ToUInt64(buf, 0);
            }

            // Last 64KB
            fs.Seek(-chunkSize, SeekOrigin.End);
            for (long i = 0; i < chunkSize / 8; i++)
            {
                if (fs.Read(buf, 0, 8) != 8) break;
                hash += BitConverter.ToUInt64(buf, 0);
            }

            return (hash.ToString("x16"), fileSize);
        }
        catch { return (null, 0); }
    }

    // ── Search ────────────────────────────────────────────────────────────────

    public record SubtitleSearchResult(
        int    FileId,
        string FileName,
        string LanguageCode,    // "en"
        string LanguageName,    // "English"
        float  Rating,          // 0-10
        int    DownloadCount,
        bool   HearingImpaired,
        bool   ForeignPartsOnly, // = Forced
        string UploadedAt,
        string Release,         // source release name
        string Format,          // "srt", "ass" etc.
        int    SubfileId
    );

    /// <summary>Search for subtitles using IMDb ID (preferred) or title+year.</summary>
    public async Task<(List<SubtitleSearchResult> results, string? error)> SearchAsync(
        string apiKey,
        string? imdbId       = null,
        string? title        = null,
        string? year         = null,
        string? languages    = null,    // comma-separated ISO 639-1: "en,fr"
        bool   isEpisode     = false,
        int?   season        = null,
        int?   episode       = null,
        string? fileHash     = null,    // OpenSubtitles hash — preferred over title search
        long    fileSize     = 0,
        CancellationToken ct = default)
    {
        try
        {
            var qs = new System.Text.StringBuilder("?");

            // Hash search is most accurate — try it first if available
            if (!string.IsNullOrWhiteSpace(fileHash) && fileSize > 0)
            {
                qs.Append($"moviehash={fileHash}&");
            }
            else if (!string.IsNullOrWhiteSpace(imdbId))
            {
                // Strip "tt" prefix for the query, OpenSubtitles wants numeric
                var numericId = imdbId.TrimStart('t').TrimStart('T');
                if (long.TryParse(numericId, out _))
                    qs.Append($"imdb_id={numericId}&");
            }
            else if (!string.IsNullOrWhiteSpace(title))
            {
                qs.Append($"query={Uri.EscapeDataString(title)}&");
                if (!string.IsNullOrWhiteSpace(year)) qs.Append($"year={year}&");
            }

            if (!string.IsNullOrWhiteSpace(languages))
                qs.Append($"languages={Uri.EscapeDataString(languages)}&");

            if (isEpisode && season.HasValue) qs.Append($"season_number={season.Value}&");
            if (isEpisode && episode.HasValue) qs.Append($"episode_number={episode.Value}&");

            qs.Append("per_page=30");

            var req = new HttpRequestMessage(HttpMethod.Get, $"{Base}/subtitles{qs}");
            req.Headers.Add("Api-Key", apiKey);

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                return (new(), $"HTTP {(int)resp.StatusCode} — {body}");
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var data = doc.RootElement.GetProperty("data");

            var results = new List<SubtitleSearchResult>();
            foreach (var item in data.EnumerateArray())
            {
                try
                {
                    var attr  = item.GetProperty("attributes");
                    var files = attr.GetProperty("files");
                    if (files.GetArrayLength() == 0) continue;
                    var file  = files[0];

                    var langObj = attr.TryGetProperty("language", out var l)
                        ? l.GetString() ?? string.Empty : string.Empty;
                    var release = attr.TryGetProperty("release", out var rel)
                        ? rel.GetString() ?? string.Empty : string.Empty;
                    var rating  = attr.TryGetProperty("ratings", out var r)
                        ? (float)(r.GetDouble()) : 0f;
                    var dlCount = attr.TryGetProperty("download_count", out var dc)
                        ? dc.GetInt32() : 0;
                    var hi      = attr.TryGetProperty("hearing_impaired", out var hiP)
                        && hiP.GetBoolean();
                    var forced  = attr.TryGetProperty("foreign_parts_only", out var fp)
                        && fp.GetBoolean();
                    var uploaded = attr.TryGetProperty("upload_date", out var ud)
                        ? ud.GetString() ?? string.Empty : string.Empty;
                    var fmt     = file.TryGetProperty("ext", out var ext)
                        ? ext.GetString() ?? "srt" : "srt";
                    var fName   = file.TryGetProperty("file_name", out var fn)
                        ? fn.GetString() ?? string.Empty : string.Empty;
                    var fileId  = file.TryGetProperty("file_id", out var fi)
                        ? fi.GetInt32() : 0;
                    var subId   = item.TryGetProperty("id", out var sid)
                        ? int.TryParse(sid.GetString(), out var sidInt) ? sidInt : 0 : 0;

                    results.Add(new SubtitleSearchResult(
                        FileId:          fileId,
                        FileName:        fName,
                        LanguageCode:    langObj,
                        LanguageName:    LangName(langObj),
                        Rating:          rating,
                        DownloadCount:   dlCount,
                        HearingImpaired: hi,
                        ForeignPartsOnly:forced,
                        UploadedAt:      uploaded,
                        Release:         release,
                        Format:          fmt,
                        SubfileId:       subId
                    ));
                }
                catch { /* skip malformed entry */ }
            }

            return (results, null);
        }
        catch (OperationCanceledException) { return (new(), "Cancelled."); }
        catch (Exception ex)              { return (new(), ex.Message); }
    }

    // ── Download ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Downloads a subtitle file to disk.
    /// Returns the path of the saved file, or an error string.
    /// OpenSubtitles free tier: 5 downloads/day.
    /// </summary>
    public async Task<(string? savedPath, string? error)> DownloadAsync(
        string apiKey,
        int    fileId,
        string destinationFolder,
        string videoBaseFileName,
        string languageCode,
        string format,
        bool   isForced,
        bool   isHearingImpaired,
        CancellationToken ct = default)
    {
        try
        {
            // Step 1: Request a download link
            var body = JsonSerializer.Serialize(new { file_id = fileId });
            var req1 = new HttpRequestMessage(HttpMethod.Post, $"{Base}/download")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
            req1.Headers.Add("Api-Key", apiKey);

            using var resp1 = await _http.SendAsync(req1, ct);
            if (!resp1.IsSuccessStatusCode)
            {
                var err = await resp1.Content.ReadAsStringAsync(ct);
                if (resp1.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    return (null, "Daily download limit reached (5/day for free tier). Try tomorrow.");
                return (null, $"HTTP {(int)resp1.StatusCode} — {err}");
            }

            var json1 = await resp1.Content.ReadAsStringAsync(ct);
            using var doc1 = JsonDocument.Parse(json1);
            var link      = doc1.RootElement.GetProperty("link").GetString()!;
            var remaining = doc1.RootElement.TryGetProperty("remaining", out var rem)
                ? rem.GetInt32() : -1;

            // Step 2: Download the file from the link
            var req2  = new HttpRequestMessage(HttpMethod.Get, link);
            using var resp2 = await _http.SendAsync(req2, ct);
            resp2.EnsureSuccessStatusCode();

            var bytes = await resp2.Content.ReadAsByteArrayAsync(ct);

            // Build filename: VideoBase.{lang}.{forced.}{hi.}{ext}
            var suffix = new System.Text.StringBuilder();
            if (!string.IsNullOrWhiteSpace(languageCode)) suffix.Append($".{languageCode}");
            if (isForced)           suffix.Append(".forced");
            if (isHearingImpaired)  suffix.Append(".hi");
            suffix.Append($".{format.TrimStart('.')}");

            var fileName  = videoBaseFileName + suffix;
            var savedPath = Path.Combine(destinationFolder, fileName);

            // Avoid overwriting — add suffix if collision
            int i = 1;
            while (File.Exists(savedPath))
            {
                savedPath = Path.Combine(destinationFolder,
                    videoBaseFileName + suffix.ToString().Replace($".{format}", $"_{i}.{format}"));
                i++;
            }

            await File.WriteAllBytesAsync(savedPath, bytes, ct);

            var remNote = remaining >= 0 ? $" ({remaining} downloads remaining today)" : string.Empty;
            return (savedPath, null);
        }
        catch (OperationCanceledException) { return (null, "Cancelled."); }
        catch (Exception ex)              { return (null, ex.Message); }
    }

    // ── Language name lookup ──────────────────────────────────────────────────

    private static readonly Dictionary<string, string> LangNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            {"en","English"},  {"fr","French"},   {"de","German"},   {"es","Spanish"},
            {"it","Italian"},  {"pt","Portuguese"},{"ja","Japanese"}, {"ko","Korean"},
            {"zh","Chinese"},  {"ru","Russian"},   {"ar","Arabic"},   {"nl","Dutch"},
            {"sv","Swedish"},  {"da","Danish"},    {"fi","Finnish"},  {"no","Norwegian"},
            {"pl","Polish"},   {"cs","Czech"},     {"sk","Slovak"},   {"hu","Hungarian"},
            {"ro","Romanian"}, {"tr","Turkish"},   {"he","Hebrew"},   {"hi","Hindi"},
            {"th","Thai"},     {"vi","Vietnamese"},{"id","Indonesian"},{"ms","Malay"},
            {"uk","Ukrainian"},{"bg","Bulgarian"}, {"hr","Croatian"}, {"sr","Serbian"},
            {"ca","Catalan"},  {"el","Greek"},     {"fa","Persian"},  {"lt","Lithuanian"},
        };

    private static string LangName(string code) =>
        LangNames.TryGetValue(code, out var n) ? n : code.ToUpperInvariant();
}
