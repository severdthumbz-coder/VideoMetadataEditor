using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using VideoMetadataEditor.Models;

namespace VideoMetadataEditor.Services;

/// <summary>
/// Hybrid three-tier library scan cache.
///
/// Architecture:
///   Tier 1 — In-memory session cache   (already exists in LibraryEntries collection)
///   Tier 2 — Persistent file-stat cache (this service — JSON file, no artwork)
///   Tier 3 — Full TagLib# read          (LibraryScanService.BuildEntry — fallback only)
///
/// Cache key:   (FilePath, FileSizeBytes, LastWriteTimeUtc)
/// Cache value: All LibraryEntry fields EXCEPT CoverArt (stored separately via artwork pass)
///
/// Cache file:  %AppDir%\library_cache_[8-char folder hash].json
/// One cache file per library folder path — handles folder changes cleanly.
///
/// Invalidation:
///   - File size or mtime changed  → cache miss → full TagLib# read
///   - File no longer exists       → entry silently dropped on save
///   - Cache file corrupt / wrong version → discarded, full scan, rebuilt
///   - Library folder path changed → different cache file name, old file ignored
/// </summary>
public class LibraryCacheService
{
    // ── Schema version — bump when LibraryEntry adds/removes fields ───────────
    private const int CacheVersion = 4;

    // ── Write lock prevents concurrent cache saves ─────────────────────────────
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    // ── In-memory index rebuilt from the cache file on Load ───────────────────
    // Key: normalised file path (lower-invariant on Windows)
    private Dictionary<string, CacheEntry> _index = new(StringComparer.OrdinalIgnoreCase);

    // ── Cache file path ───────────────────────────────────────────────────────
    private string? _currentCachePath;

    // ─────────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true when a non-empty cache is loaded and ready.
    /// Shown in the Settings tab so the user knows when Clear Cache does something.
    /// </summary>
    public bool HasCache => _index.Count > 0;

    /// <summary>Number of entries in the current in-memory cache.</summary>
    public int CachedEntryCount => _index.Count;

    /// <summary>
    /// Loads the cache file for <paramref name="libraryFolder"/> into memory.
    /// Call once before a scan. Silently rebuilds from scratch on any error.
    /// </summary>
    public void Load(string libraryFolder)
    {
        _currentCachePath = CacheFilePath(libraryFolder);
        _index.Clear();

        if (!File.Exists(_currentCachePath)) return;

        try
        {
            var json = File.ReadAllText(_currentCachePath, Encoding.UTF8);
            var file = JsonConvert.DeserializeObject<CacheFile>(json);

            if (file == null || file.Version != CacheVersion)
            {
                // Version mismatch — discard silently; will rebuild
                _index.Clear();
                return;
            }

            _index = file.Entries.ToDictionary(
                e => e.FilePath,
                e => e,
                StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            // Corrupt file — discard silently; will rebuild from full scan
            _index.Clear();
        }
    }

    /// <summary>
    /// Tries to retrieve a cached <see cref="LibraryEntry"/> for <paramref name="path"/>.
    /// Returns null (cache miss) if the file is new, modified, or not previously cached.
    /// </summary>
    public LibraryEntry? TryGet(string path, long fileSizeBytes, DateTime lastWriteUtc)
    {
        if (!_index.TryGetValue(path, out var cached)) return null;

        // Validate freshness — size + mtime must match exactly
        if (cached.FileSizeBytes != fileSizeBytes) return null;
        if (Math.Abs((cached.LastWriteUtc - lastWriteUtc).TotalSeconds) > 2) return null;

        return cached.ToLibraryEntry();
    }

    /// <summary>
    /// Upserts <paramref name="entry"/> into the in-memory index.
    /// Call after every successful TagLib# read so the next scan can use it.
    /// </summary>
    public void Put(string path, long fileSizeBytes, DateTime lastWriteUtc, LibraryEntry entry)
    {
        _index[path] = CacheEntry.FromLibraryEntry(entry, fileSizeBytes, lastWriteUtc);
    }

    /// <summary>
    /// Clears the in-memory index so every file gets a fresh read on the next scan.
    /// Call before a manual Scan Library to guarantee recently-embedded files are picked up.
    /// The disk cache is NOT cleared — it is re-populated incrementally as files are read.
    /// </summary>
    public void ClearMemoryCache() => _index.Clear();

    /// <summary>
    /// Removes entries for paths that no longer exist on disk (ghost cleanup),
    /// then persists the updated cache atomically. Call after every scan.
    /// Fire-and-forget safe — errors are swallowed so they never block the UI.
    /// </summary>
    public async Task SaveAsync(IEnumerable<string> liveFilePaths)
    {
        if (_currentCachePath == null) return;

        await _saveLock.WaitAsync().ConfigureAwait(false);
        try
        {
            // Evict ghosts — paths that no longer exist on disk
            var live = new HashSet<string>(liveFilePaths, StringComparer.OrdinalIgnoreCase);
            var stale = _index.Keys.Where(k => !live.Contains(k)).ToList();
            foreach (var k in stale) _index.Remove(k);

            var file = new CacheFile
            {
                Version = CacheVersion,
                Entries = _index.Values.ToList()
            };

            var json    = JsonConvert.SerializeObject(file, Formatting.None);
            var tmpPath = _currentCachePath + ".tmp";
            await File.WriteAllTextAsync(tmpPath, json, Encoding.UTF8).ConfigureAwait(false);
            File.Replace(tmpPath, _currentCachePath, null);
        }
        catch { /* cache save failure is non-fatal */ }
        finally { _saveLock.Release(); }
    }

    /// <summary>Deletes the cache file for the given folder and clears the in-memory index.</summary>
    public void Clear(string libraryFolder)
    {
        _index.Clear();
        var path = CacheFilePath(libraryFolder);
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// <summary>Deletes ALL cache files created by this app (all library folders).</summary>
    public static void ClearAll()
    {
        var dir = AppDomain.CurrentDomain.BaseDirectory;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "library_cache_*.json"))
                File.Delete(f);
        }
        catch { }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string CacheFilePath(string folder)
    {
        // Hash the folder path to a short suffix — keeps filenames safe on all OSes.
        // 8 hex chars give 4 billion combinations, enough to avoid collisions.
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(folder.ToLowerInvariant()));
        var suffix = Convert.ToHexString(hash)[..8].ToLowerInvariant();
        return Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            $"library_cache_{suffix}.json");
    }

    // ── Serialisation types ───────────────────────────────────────────────────

    private class CacheFile
    {
        public int Version { get; set; }
        public List<CacheEntry> Entries { get; set; } = new();
    }

    private class CacheEntry
    {
        // Identity / freshness key
        public string   FilePath       { get; set; } = string.Empty;
        public long     FileSizeBytes  { get; set; }
        public DateTime LastWriteUtc   { get; set; }

        // LibraryEntry fields — NO CoverArt (loaded separately by artwork pass)
        public string   Title          { get; set; } = string.Empty;
        public string   Year           { get; set; } = string.Empty;
        public string   Genre          { get; set; } = string.Empty;
        public string   Director       { get; set; } = string.Empty;
        public string   Cast           { get; set; } = string.Empty;
        public string   Description    { get; set; } = string.Empty;
        public string   ImdbId         { get; set; } = string.Empty;
        public string   TmdbId         { get; set; } = string.Empty;
        public float    ImdbRating     { get; set; }
        public string   MpaRating      { get; set; } = string.Empty;
        public bool     IsEpisode      { get; set; }
        public string   ShowTitle      { get; set; } = string.Empty;
        public int?     Season         { get; set; }
        public int?     Episode        { get; set; }
        public string   EpisodeTitle   { get; set; } = string.Empty;
        public string   AiredDate      { get; set; } = string.Empty;
        public string   TmdbSeriesId   { get; set; } = string.Empty;
        public string   Duration       { get; set; } = string.Empty;
        public string   VideoCodec     { get; set; } = string.Empty;
        public string   AudioCodec     { get; set; } = string.Empty;
        public string   Resolution     { get; set; } = string.Empty;
        public string   Format         { get; set; } = string.Empty;
        public DateTime? DownloadDate  { get; set; }

        public LibraryEntry ToLibraryEntry() => new()
        {
            FilePath      = FilePath,
            FileSizeBytes = FileSizeBytes,
            Title         = Title,
            Year          = Year,
            Genre         = Genre,
            Director      = Director,
            Cast          = Cast,
            Description   = Description,
            ImdbId        = ImdbId,
            TmdbId        = TmdbId,
            ImdbRating    = ImdbRating,
            MpaRating     = MpaRating,
            IsEpisode     = IsEpisode,
            ShowTitle     = ShowTitle,
            Season        = Season,
            Episode       = Episode,
            EpisodeTitle  = EpisodeTitle,
            AiredDate     = AiredDate,
            TmdbSeriesId  = TmdbSeriesId,
            Duration      = Duration,
            VideoCodec    = VideoCodec,
            AudioCodec    = AudioCodec,
            Resolution    = Resolution,
            Format        = Format,
            DownloadDate  = DownloadDate,
            // CoverArt intentionally null — loaded by artwork pass
        };

        public static CacheEntry FromLibraryEntry(
            LibraryEntry e, long sizeBytes, DateTime mtime) => new()
        {
            FilePath      = e.FilePath,
            FileSizeBytes = sizeBytes,
            LastWriteUtc  = mtime,
            Title         = e.Title,
            Year          = e.Year,
            Genre         = e.Genre,
            Director      = e.Director,
            Cast          = e.Cast,
            Description   = e.Description,
            ImdbId        = e.ImdbId,
            TmdbId        = e.TmdbId,
            ImdbRating    = e.ImdbRating,
            MpaRating     = e.MpaRating,
            IsEpisode     = e.IsEpisode,
            ShowTitle     = e.ShowTitle,
            Season        = e.Season,
            Episode       = e.Episode,
            EpisodeTitle  = e.EpisodeTitle,
            AiredDate     = e.AiredDate,
            TmdbSeriesId  = e.TmdbSeriesId,
            Duration      = e.Duration,
            VideoCodec    = e.VideoCodec,
            AudioCodec    = e.AudioCodec,
            Resolution    = e.Resolution,
            Format        = e.Format,
            DownloadDate  = e.DownloadDate,
        };
    }
}
