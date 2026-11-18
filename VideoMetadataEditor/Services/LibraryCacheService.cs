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

    // ── One index per library folder (build 176) ──────────────────────────────
    // Until build 175 there was a single index, replaced on every Load. With two library
    // folders (e.g. Movies + TV Shows) a scan loaded A, then B — throwing A away. Entries
    // for A then couldn't be updated (track info, embeds) and A's in-memory changes were
    // lost on each alternate reload. Now every folder keeps its own index for the session,
    // and each file is routed to the folder that contains it.
    private sealed class FolderCache
    {
        public required string Folder    { get; init; }   // normalised, with trailing separator
        public required string CachePath { get; init; }
        // ConcurrentDictionary — the scan Put()s from parallel tasks.
        public System.Collections.Concurrent.ConcurrentDictionary<string, CacheEntry> Index { get; set; }
            = new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, FolderCache> _folders
        = new(StringComparer.OrdinalIgnoreCase);

    private static string FolderKey(string folder)
    {
        var f = folder.Trim();
        try { f = Path.GetFullPath(f); } catch { }
        return f.EndsWith(Path.DirectorySeparatorChar) || f.EndsWith(Path.AltDirectorySeparatorChar)
            ? f : f + Path.DirectorySeparatorChar;
    }

    /// <summary>The loaded folder that contains <paramref name="path"/> (deepest match), or null.</summary>
    private FolderCache? FolderFor(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        FolderCache? best = null;
        foreach (var fc in _folders.Values)
            if (path.StartsWith(fc.Folder, StringComparison.OrdinalIgnoreCase) &&
                (best == null || fc.Folder.Length > best.Folder.Length))
                best = fc;
        return best;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Public API
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns true when a non-empty cache is loaded and ready.
    /// Shown in the Settings tab so the user knows when Clear Cache does something.
    /// </summary>
    public bool HasCache => _folders.Values.Any(f => !f.Index.IsEmpty);

    /// <summary>Number of entries cached across all loaded library folders.</summary>
    public int CachedEntryCount => _folders.Values.Sum(f => f.Index.Count);

    /// <summary>
    /// Loads the cache for <paramref name="libraryFolder"/> from disk the first time it's
    /// needed this session. Later calls are no-ops, preserving in-memory updates (e.g.
    /// from SyncLibraryEntry after an embed) that haven't been saved yet. Other folders'
    /// caches stay loaded.
    /// </summary>
    public void LoadIfNeeded(string libraryFolder)
    {
        if (_folders.ContainsKey(FolderKey(libraryFolder))) return;
        Load(libraryFolder);
    }

    /// <summary>(Re)loads one folder's cache from disk. Silently starts empty on any error.</summary>
    public void Load(string libraryFolder)
    {
        var key = FolderKey(libraryFolder);
        var fc = new FolderCache { Folder = key, CachePath = CacheFilePath(libraryFolder) };
        _folders[key] = fc;

        if (!File.Exists(fc.CachePath)) return;
        try
        {
            var json = File.ReadAllText(fc.CachePath, Encoding.UTF8);
            var file = JsonConvert.DeserializeObject<CacheFile>(json);
            if (file == null || file.Version != CacheVersion) return;   // version mismatch — rebuild

            fc.Index = new System.Collections.Concurrent.ConcurrentDictionary<string, CacheEntry>(
                file.Entries
                    .GroupBy(e => e.FilePath, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase);
        }
        catch { /* corrupt file — start empty; rebuilt from the next full scan */ }
    }

    /// <summary>
    /// Waits until any in-progress SaveAsync has completed.
    /// Call this before Load() to ensure fire-and-forget saves from SyncLibraryEntry
    /// have flushed to disk before the scan reloads the cache file.
    /// </summary>
    public async Task FlushAsync()
    {
        await _saveLock.WaitAsync().ConfigureAwait(false);
        _saveLock.Release();
    }

    /// <summary>
    /// Tries to retrieve a cached <see cref="LibraryEntry"/> for <paramref name="path"/>.
    /// Returns null (cache miss) if the file is new, modified, or not previously cached.
    /// </summary>
    public LibraryEntry? TryGet(string path, long fileSizeBytes, DateTime lastWriteUtc)
    {
        var fc = FolderFor(path);
        if (fc == null || !fc.Index.TryGetValue(path, out var cached)) return null;

        // Validate freshness — size + mtime must match exactly
        if (cached.FileSizeBytes != fileSizeBytes) return null;
        if (Math.Abs((cached.LastWriteUtc - lastWriteUtc).TotalSeconds) > 2) return null;

        return cached.ToLibraryEntry();
    }

    /// <summary>
    /// Removes a path from the in-memory cache so the next scan performs a fresh TagLib#
    /// read. Used after remux Replace Original and renames.
    /// </summary>
    public void Evict(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        foreach (var fc in _folders.Values) fc.Index.TryRemove(path, out _);
    }

    /// <summary>
    /// Upserts <paramref name="entry"/> into the cache of the library folder that contains
    /// it. Files outside every loaded library folder aren't cached (they're not in the
    /// library). Call after every successful TagLib# read so the next scan can use it.
    /// </summary>
    public void Put(string path, long fileSizeBytes, DateTime lastWriteUtc, LibraryEntry entry)
    {
        var fc = FolderFor(path);
        if (fc == null) return;
        fc.Index[path] = CacheEntry.FromLibraryEntry(entry, fileSizeBytes, lastWriteUtc);
    }

    /// <summary>
    /// Build 175: stores the audio/subtitle tracks read for <paramref name="path"/> on its
    /// existing cache entry, without touching anything else. The entry keeps its size and
    /// modified-time key, so if the file changes the tracks are read again with the tags.
    /// Returns false when the file isn't cached (nothing to update).
    /// </summary>
    public bool UpdateTracks(string path, string? tracks)
    {
        var fc = FolderFor(path);
        if (fc == null || !fc.Index.TryGetValue(path, out var cached)) return false;
        cached.Tracks = tracks;
        return true;
    }

    /// <summary>
    /// Clears the in-memory caches so every file gets a fresh read on the next scan.
    /// The disk caches are NOT cleared — they are re-populated as files are read.
    /// </summary>
    public void ClearMemoryCache()
    {
        foreach (var fc in _folders.Values) fc.Index.Clear();
    }

    /// <summary>
    /// Removes entries for paths that are no longer live (ghost cleanup), then persists the
    /// cache files atomically. Only folders that own at least one of
    /// <paramref name="liveFilePaths"/> are pruned and saved — so a single-folder scan
    /// never empties another folder's cache, and a library drive that's offline (none of
    /// its files in the list) keeps its cache. Fire-and-forget safe.
    /// </summary>
    public async Task SaveAsync(IEnumerable<string> liveFilePaths)
    {
        if (_folders.IsEmpty) return;

        await _saveLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var liveByFolder = new Dictionary<FolderCache, HashSet<string>>();
            foreach (var p in liveFilePaths)
            {
                var fc = FolderFor(p);
                if (fc == null) continue;
                if (!liveByFolder.TryGetValue(fc, out var set))
                    liveByFolder[fc] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(p);
            }

            foreach (var (fc, live) in liveByFolder)
            {
                var stale = fc.Index.Keys.Where(k => !live.Contains(k)).ToList();
                foreach (var k in stale) fc.Index.TryRemove(k, out _);
                await WriteFolderAsync(fc).ConfigureAwait(false);
            }
        }
        finally { _saveLock.Release(); }
    }

    private static async Task WriteFolderAsync(FolderCache fc)
    {
        var tmpPath = fc.CachePath + ".tmp";
        try
        {
            var file = new CacheFile { Version = CacheVersion, Entries = fc.Index.Values.ToList() };
            var json = JsonConvert.SerializeObject(file, Formatting.None);
            await File.WriteAllTextAsync(tmpPath, json, Encoding.UTF8).ConfigureAwait(false);

            // File.Replace requires the destination to exist — on the first save it doesn't.
            if (File.Exists(fc.CachePath)) File.Replace(tmpPath, fc.CachePath, null);
            else File.Move(tmpPath, fc.CachePath, overwrite: true);
        }
        catch (Exception ex)
        {
            // Non-fatal (the library rebuilds on the next scan), but not silent.
            LogService.Warn("Library", $"Couldn't save the library cache: {ex.Message}", ex.ToString(), fc.CachePath);
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
        }
    }

    /// <summary>Deletes the cache file for the given folder and clears its in-memory index.</summary>
    public void Clear(string libraryFolder)
    {
        if (_folders.TryRemove(FolderKey(libraryFolder), out var fc)) fc.Index.Clear();
        var path = CacheFilePath(libraryFolder);
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    /// <summary>Build 176: clears every loaded folder's cache and deletes every cache file.</summary>
    public void ClearEverything()
    {
        foreach (var fc in _folders.Values) fc.Index.Clear();
        _folders.Clear();
        ClearAll();
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
        public bool     IsWatched      { get; set; }
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
        /// <summary>Build 175: encoded audio/subtitle tracks; null = not read yet (old caches too).</summary>
        public string?  Tracks         { get; set; }

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
            IsWatched     = IsWatched,
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
            Tracks        = Tracks,
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
            IsWatched     = e.IsWatched,
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
            Tracks        = e.Tracks,
        };
    }
}
