using System.IO;
using TagLib;
using VideoMetadataEditor.Models;
using SysFile = System.IO.File;
using SysPath = System.IO.Path;

namespace VideoMetadataEditor.Services;

/// <summary>
/// Scans a folder (non-recursively or recursively) and builds LibraryEntry objects
/// by reading embedded metadata and MediaInfo from each video file.
/// Reports progress as each file is processed.
/// </summary>
public class LibraryScanService
{
    // Only formats where TagLib# can write metadata AND Plex/Jellyfin reads embedded tags.
    // AVI is included — TagLib# can write basic tags and Plex/Jellyfin reads them.
    // WebM/WMV have limited support (no artwork, fewer fields) but are still scanned.
    // Everything else (.ts, .flv, .vob, .mpeg, .3gp, etc.) either has no tag support
    // or media servers ignore embedded tags entirely in favour of NFO sidecar files.
    private static readonly HashSet<string> VideoExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4",   // MPEG-4 — full tag + artwork support
            ".mkv",   // Matroska — full tag + artwork (MKVPropEdit for cover.jpg)
            ".m4v",   // iTunes MPEG-4 — same as MP4
            ".mov",   // QuickTime — same container as MP4
            ".wmv",   // Windows Media — limited tags, no artwork
            ".webm",  // WebM/Matroska — limited tags, no artwork
            ".avi",   // AVI — basic tags only, Plex/Jellyfin reads them
        };

    private readonly MetadataService _meta;
    private readonly LibraryCacheService _cache;

    public LibraryScanService(MetadataService meta, LibraryCacheService cache)
    {
        _meta  = meta;
        _cache = cache;
    }

    /// <summary>Last scan stats — exposed for status bar display.</summary>
    public int LastScanCacheHits   { get; private set; }
    public int LastScanCacheMisses { get; private set; }

    public async Task<List<LibraryEntry>> ScanFolderAsync(
        string folder,
        bool recursive,
        IProgress<(int done, int total, string current)>? progress = null,
        CancellationToken ct = default)
    {
        // ── Phase 1: collect file paths + stat info (fast — no video file opens) ───
        var fileInfos = await Task.Run(() =>
        {
            var opt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var list = new List<FileStatInfo>();
            foreach (var f in Directory.EnumerateFiles(folder, "*.*", opt))
            {
                if (!VideoExtensions.Contains(SysPath.GetExtension(f))) continue;
                if (SysPath.GetFileName(f).StartsWith(".vme_tmp_", StringComparison.OrdinalIgnoreCase)) continue;
                if (SysPath.GetFileName(f).StartsWith(".vme_bak_", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var fi = new FileInfo(f);
                    if (fi.Length > 0)
                        list.Add(new FileStatInfo(f, fi.Length, fi.LastWriteTimeUtc));
                }
                catch { }
            }
            list.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
            return list;
        }, ct);

        int total = fileInfos.Count;

        // ── Phase 2: load cache for this folder ───────────────────────────────────
        await Task.Run(() => _cache.Load(folder), ct);

        // ── Phase 3: parallel scan — cache hits skip TagLib#, misses do full read ─
        int workers = Math.Min(Environment.ProcessorCount, 8);
        using var sem = new SemaphoreSlim(workers);

        var results    = new System.Collections.Concurrent.ConcurrentBag<(int idx, LibraryEntry entry)>();
        int completed  = 0;
        int cacheHits  = 0;
        int cacheMisses = 0;

        var tasks = fileInfos.Select(async (fi, idx) =>
        {
            await sem.WaitAsync(ct);
            try
            {
                if (ct.IsCancellationRequested) return;

                LibraryEntry entry;

                var cached = _cache.TryGet(fi.Path, fi.Size, fi.Mtime);
                if (cached != null)
                {
                    // ── Cache HIT — no TagLib# call needed ────────────────────────
                    entry = cached;
                    System.Threading.Interlocked.Increment(ref cacheHits);
                }
                else
                {
                    // ── Cache MISS — full read, then store in cache ───────────────
                    entry = await Task.Run(() => BuildEntry(fi.Path), ct);
                    _cache.Put(fi.Path, fi.Size, fi.Mtime, entry);
                    System.Threading.Interlocked.Increment(ref cacheMisses);
                }

                results.Add((idx, entry));
                int done = System.Threading.Interlocked.Increment(ref completed);
                progress?.Report((done, total, SysPath.GetFileName(fi.Path)));
            }
            finally { sem.Release(); }
        });

        await Task.WhenAll(tasks);

        LastScanCacheHits   = cacheHits;
        LastScanCacheMisses = cacheMisses;

        var ordered = results.OrderBy(r => r.idx).Select(r => r.entry).ToList();

        // ── Phase 4: persist updated cache (fire-and-forget) ─────────────────────
        var livePaths = fileInfos.Select(f => f.Path);
        _ = _cache.SaveAsync(livePaths);

        return ordered;
    }

    private LibraryEntry BuildEntry(string path)
    {
        var info    = new FileInfo(path);
        var meta    = _meta.ReadMetadata(path);   // full read — includes artwork bytes
        var (parsedTitle, parsedYear) = FilenameParser.Parse(SysPath.GetFileNameWithoutExtension(path));

        string title = !string.IsNullOrWhiteSpace(meta.Title) ? meta.Title
                     : !string.IsNullOrWhiteSpace(parsedTitle) ? parsedTitle
                     : SysPath.GetFileNameWithoutExtension(path);

        string year  = !string.IsNullOrWhiteSpace(meta.Year) ? meta.Year
                     : !string.IsNullOrWhiteSpace(parsedYear) ? parsedYear
                     : string.Empty;

        // Duration + codec from TagLib
        string duration = string.Empty;
        string videoCodec = string.Empty;
        string audioCodec = string.Empty;
        string resolution = string.Empty;
        string format     = SysPath.GetExtension(path).TrimStart('.').ToUpperInvariant();

        try
        {
            using var tf = TagLib.File.Create(path);
            var ts = tf.Properties.Duration;
            duration  = ts.TotalHours >= 1
                ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
                : $"{ts.Minutes}:{ts.Seconds:D2}";

            var codec = tf.Properties.Codecs.OfType<TagLib.IVideoCodec>().FirstOrDefault();
            if (codec != null)
            {
                videoCodec = codec.Description;
                resolution = $"{codec.VideoWidth}×{codec.VideoHeight}";
            }
            var acodec = tf.Properties.Codecs.OfType<TagLib.IAudioCodec>().FirstOrDefault();
            if (acodec != null)
                audioCodec = acodec.Description;
        }
        catch { }

        return new LibraryEntry
        {
            FilePath      = path,
            FileSizeBytes = info.Length,
            Title         = title,
            Year          = year,
            Genre         = meta.Genre,
            Director      = meta.Director,
            Cast          = meta.Cast,
            Description   = meta.Description,
            ImdbId        = meta.ImdbId,
            TmdbId        = meta.TmdbId,
            ImdbRating    = meta.Rating,
            MpaRating     = meta.MpaRating,
            IsEpisode     = meta.IsEpisode,
            ShowTitle     = meta.ShowTitle,
            Season        = meta.Season,
            Episode       = meta.Episode,
            EpisodeTitle  = meta.EpisodeTitle,
            AiredDate     = meta.AiredDate,
            TmdbSeriesId  = meta.TmdbSeriesId,
            CoverArt      = meta.ArtworkBytes,
            Duration      = duration,
            VideoCodec    = videoCodec,
            AudioCodec    = audioCodec,
            Resolution    = resolution,
            Format        = format,
            DownloadDate  = info.CreationTime == default ? null : info.CreationTime,
        };
    }

    /// <summary>
    /// Loads artwork bytes into existing <see cref="LibraryEntry"/> items.
    /// Call after <see cref="ScanFolderAsync"/> completes to populate cover art
    /// without blocking the initial scan. Reports progress per file.
    /// </summary>
    public async Task LoadArtworkAsync(
        IReadOnlyList<LibraryEntry> entries,
        int workers = 4,
        IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        int done = 0;
        using var sem = new SemaphoreSlim(workers);
        var tasks = entries.Select(async entry =>
        {
            await sem.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                if (entry.CoverArt is { Length: > 0 }) return; // already loaded
                var art = await Task.Run(() => _meta.ReadArtworkOnly(entry.FilePath), ct);
                if (art is { Length: > 0 })
                    entry.CoverArt = art;
            }
            catch (OperationCanceledException) { throw; }
            catch { }
            finally
            {
                sem.Release();
                progress?.Report(System.Threading.Interlocked.Increment(ref done));
            }
        }).ToList();
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Builds a single <see cref="LibraryEntry"/> for a newly-detected file.
    /// Used by the library watch service to add files without a full re-scan.
    /// Returns null if the file is not a supported video or cannot be read.
    /// </summary>
    public async Task<LibraryEntry?> BuildSingleEntryAsync(string path)
    {
        if (!VideoExtensions.Contains(SysPath.GetExtension(path))) return null;
        if (SysPath.GetFileName(path).StartsWith(".vme_", StringComparison.OrdinalIgnoreCase)) return null;

        return await Task.Run(() =>
        {
            try { return BuildEntry(path); }
            catch { return null; }
        });
    }
}

/// <summary>Lightweight stat record used in Phase 1 of ScanFolderAsync.</summary>
internal sealed class FileStatInfo
{
    public string   Path  { get; }
    public long     Size  { get; }
    public DateTime Mtime { get; }
    public FileStatInfo(string path, long size, DateTime mtime)
    {
        Path  = path;
        Size  = size;
        Mtime = mtime;
    }
}
