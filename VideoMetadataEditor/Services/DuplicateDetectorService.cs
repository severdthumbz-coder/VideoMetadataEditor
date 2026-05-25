using System.IO;
using System.Security.Cryptography;
using VideoMetadataEditor.Models;

namespace VideoMetadataEditor.Services;

// ── Confidence level for a duplicate group ────────────────────────────────────

public enum DuplicateConfidence
{
    Exact,         // same size + same partial hash (99.9% certain)
    Strong,        // same size + matching title + year (very likely)
    Probable,      // same size + matching title (probable)
    Weak,          // same size only (possible — different encodes of same file)
    CrossSize,     // same IMDb/TMDB ID or title+year, DIFFERENT file size
                   // e.g. same movie, different encode/quality
    AudioMatch     // same audio fingerprint (Chromaprint deep scan)
                   // catches renamed files, re-encodes, different containers
}

// ── Result types ──────────────────────────────────────────────────────────────

public record DuplicateGroup(
    IReadOnlyList<VideoFile> Files,
    DuplicateConfidence Confidence,
    string Reason)
{
    public string Label =>
        $"{Files.Count} copies  ·  {ConfidenceLabel}  ·  {FormatSize(Files[0].FileSizeBytes)}";

    public string ConfidenceLabel => Confidence switch
    {
        DuplicateConfidence.Exact     => "✓✓ Exact match (hash+size)",
        DuplicateConfidence.Strong    => "✓ Strong match (size+title+year)",
        DuplicateConfidence.Probable  => "≈ Probable match (size+title)",
        DuplicateConfidence.Weak             => "? Weak match (size only)",
        DuplicateConfidence.CrossSize     => "⚠ Alternate version (same identity, different size)",
        DuplicateConfidence.AudioMatch    => "♪ Audio match (deep scan)",
        _                             => "Unknown"
    };

    private static string FormatSize(long b) =>
        b < 1024 * 1024       ? $"{b / 1024.0:F1} KB"
        : b < 1024L*1024*1024 ? $"{b / (1024.0*1024):F1} MB"
                                : $"{b / (1024.0*1024*1024):F2} GB";
}

// ── Service ───────────────────────────────────────────────────────────────────

public static class DuplicateDetectorService
{
    // Session-level hash cache — entries survive across multiple DetectAsync calls.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string>
        _hashCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Clears the hash cache (call after file modifications to prevent stale hits).</summary>
    public static void ClearHashCache() => _hashCache.Clear();

    // Partial hash: read this many bytes from start + end of each file.
    // Fast enough even for 2 GB files; catches bit-for-bit identical copies.
    private const int HashSampleBytes = 512 * 1024; // 512 KB per endpoint

    // Size tolerance: files within this fraction of each other are size-equal
    private const double SizeTolerance = 0.001; // 0.1%

    // ── Main entry point ──────────────────────────────────────────────────────

    /// <summary>
    /// Multi-stage duplicate detection:
    ///   Stage 1 — group by file size (fast, no IO beyond what we have)
    ///   Stage 2 — within size groups, compare normalised metadata (title + year + imdb/tmdb)
    ///   Stage 3 — for groups that survive stage 2, compute a partial hash for certainty
    /// Progress reports (current file name, filesChecked, totalToCheck).
    /// </summary>
    public static async Task<IReadOnlyList<DuplicateGroup>> DetectAsync(
        IEnumerable<VideoFile> files,
        bool useHashing = true,
        IProgress<(string current, int done, int total)>? progress = null,
        CancellationToken ct = default)
    {
        // Honour excluded patterns (same set as Deep Scan) so files the user has
        // configured to ignore don't appear as duplicates in Quick Scan results.
        var excludedPatterns = BuildExcludedPatterns(
            App.ConfigService.Settings.DuplicateExcludedPatterns);
        var list = files
            .Where(f => f.FileSizeBytes > 0
                     && !IsExcludedByPatterns(f.FilePath, excludedPatterns))
            .ToList();
        if (list.Count < 2) return [];

        // ── Stage 1: bucket by file size ──────────────────────────────────────
        // Two files are "same size" if they differ by < 0.1% — handles
        // trivially re-encoded copies that differ by a few bytes.
        var sizeBuckets = list
            .GroupBy(f => SizeBucket(f.FileSizeBytes))
            .Where(g => g.Count() > 1)
            .ToList();

        if (sizeBuckets.Count == 0) return [];

        // ── Stage 2: metadata scoring within each size bucket ─────────────────
        var candidates = new List<(IReadOnlyList<VideoFile> group, DuplicateConfidence confidence, string reason)>();

        foreach (var bucket in sizeBuckets)
        {
            var bucketFiles = bucket.ToList();
            // Sub-group by metadata fingerprint
            var metaGroups = bucketFiles
                .GroupBy(f => MetadataKey(f))
                .Where(g => g.Count() > 1)
                .ToList();

            foreach (var mg in metaGroups)
            {
                var (conf, reason) = ScoreMetadataGroup(mg.Key, mg.ToList());
                candidates.Add((mg.OrderBy(f => f.FilePath).ToList(), conf, reason));
            }

            // Size-only group for files that had no metadata match
            // Use a HashSet for O(1) lookup instead of SelectMany+Contains (O(n²))
            var matchedFiles = new HashSet<VideoFile>(metaGroups.SelectMany(g => g));
            var unmatchedInMeta = bucketFiles
                .Where(f => !matchedFiles.Contains(f))
                .ToList();
            if (unmatchedInMeta.Count > 1)
                candidates.Add((unmatchedInMeta, DuplicateConfidence.Weak, "Same file size"));
        }

        // ── Stage 4: alternate-version detection (different size, same identity) ─────
        // Catches same movie at different encode quality, partial vs completed downloads,
        // and TV show episodes duplicated with different quality tags.
        var allFiles = list.ToList();
        var alreadyGrouped = new HashSet<string>(
            candidates.SelectMany(c => c.group).Select(f => f.FilePath),
            StringComparer.OrdinalIgnoreCase);

        // Group by IMDb ID (highest confidence identity marker)
        var byImdb = allFiles
            .Where(f => !string.IsNullOrWhiteSpace(f.EmbeddedMetadata?.ImdbId))
            .GroupBy(f => f.EmbeddedMetadata!.ImdbId.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToList();
        foreach (var g in byImdb)
        {
            var group = g.OrderBy(f => f.FileSizeBytes).ToList();
            // Skip if already in a same-size group — those are already handled
            if (group.Any(f => alreadyGrouped.Contains(f.FilePath))) continue;
            candidates.Add((group, DuplicateConfidence.CrossSize,
                $"Same IMDb ID ({g.Key}) — different sizes ({FormatSizes(group)})"));
            foreach (var f in group) alreadyGrouped.Add(f.FilePath);
        }

        // Group by TMDB ID
        var byTmdb = allFiles
            .Where(f => !string.IsNullOrWhiteSpace(f.EmbeddedMetadata?.TmdbId)
                     && string.IsNullOrWhiteSpace(f.EmbeddedMetadata?.ImdbId))
            .GroupBy(f => f.EmbeddedMetadata!.TmdbId.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToList();
        foreach (var g in byTmdb)
        {
            var group = g.OrderBy(f => f.FileSizeBytes).ToList();
            if (group.Any(f => alreadyGrouped.Contains(f.FilePath))) continue;
            candidates.Add((group, DuplicateConfidence.CrossSize,
                $"Same TMDB ID ({g.Key}) — different sizes ({FormatSizes(group)})"));
            foreach (var f in group) alreadyGrouped.Add(f.FilePath);
        }

        // Group by Title + Year for movies (no IMDb/TMDB ID embedded yet)
        var byTitleYear = allFiles
            .Where(f => !string.IsNullOrWhiteSpace(f.EmbeddedMetadata?.Title)
                     && !string.IsNullOrWhiteSpace(f.EmbeddedMetadata?.Year)
                     && f.EmbeddedMetadata?.IsEpisode == false
                     && string.IsNullOrWhiteSpace(f.EmbeddedMetadata?.ImdbId)
                     && string.IsNullOrWhiteSpace(f.EmbeddedMetadata?.TmdbId))
            .GroupBy(f => NormaliseTitle(f.EmbeddedMetadata!.Title) + "|" + f.EmbeddedMetadata!.Year,
                     StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToList();
        foreach (var g in byTitleYear)
        {
            var group = g.OrderBy(f => f.FileSizeBytes).ToList();
            if (group.Any(f => alreadyGrouped.Contains(f.FilePath))) continue;
            candidates.Add((group, DuplicateConfidence.CrossSize,
                $"Same title+year ({g.First().EmbeddedMetadata!.Title}, {g.First().EmbeddedMetadata!.Year}) — different sizes ({FormatSizes(group)})"));
            foreach (var f in group) alreadyGrouped.Add(f.FilePath);
        }

        // Group by ShowTitle + Season + Episode for TV episodes
        var byEpisode = allFiles
            .Where(f => f.EmbeddedMetadata?.IsEpisode == true
                     && !string.IsNullOrWhiteSpace(f.EmbeddedMetadata?.ShowTitle)
                     && f.EmbeddedMetadata?.Season > 0
                     && f.EmbeddedMetadata?.Episode > 0)
            .GroupBy(f => NormaliseTitle(f.EmbeddedMetadata!.ShowTitle!)
                        + $"|S{f.EmbeddedMetadata!.Season:D2}E{f.EmbeddedMetadata!.Episode:D2}",
                     StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .ToList();
        foreach (var g in byEpisode)
        {
            var group = g.OrderBy(f => f.FileSizeBytes).ToList();
            if (group.Any(f => alreadyGrouped.Contains(f.FilePath))) continue;
            var ep = group[0].EmbeddedMetadata!;
            candidates.Add((group, DuplicateConfidence.CrossSize,
                $"TV duplicate: {ep.ShowTitle} S{ep.Season:D2}E{ep.Episode:D2} — different sizes ({FormatSizes(group)})"));
            foreach (var f in group) alreadyGrouped.Add(f.FilePath);
        }

        if (!useHashing || candidates.Count == 0)
            return candidates
                .Select(c => new DuplicateGroup(c.group, c.confidence, c.reason))
                .OrderByDescending(g => g.Confidence)
                .ToList();

        // ── Stage 3: partial hash verification ────────────────────────────────
        // Hash only the Strong/Probable groups — Weak groups are too risky to
        // upgrade without a hash, and Exact is only set after hashing.
        var result = new List<DuplicateGroup>();
        // All groups are now hashed (Weak groups too, to eliminate false positives)
        int totalToHash = candidates
            .SelectMany(c => c.group)
            .Distinct()
            .Count();
        int hashed = 0;

        var hashCache = _hashCache; // use session-level cache

        foreach (var (group, confidence, reason) in candidates)
        {
            if (ct.IsCancellationRequested) break;

            if (confidence < DuplicateConfidence.Probable)
            {
                // Weak (size-only): hash to confirm before showing to user.
                // Without hashing, common-size files (e.g. all 700 MB DVDRips) would be
                // falsely grouped as duplicates. Hash promotes confirmed matches to Exact
                // and silently drops false positives (non-matching hashes).
                var weakHashGroups = new Dictionary<string, List<VideoFile>>();
                foreach (var vf in group)
                {
                    if (ct.IsCancellationRequested) break;
                    progress?.Report((vf.FileName, hashed, totalToHash));
                    if (!hashCache.TryGetValue(vf.FilePath, out var h))
                    {
                        h = await ComputePartialHashAsync(vf.FilePath, ct);
                        hashCache[vf.FilePath] = h;
                    }
                    hashed++;
                    if (!weakHashGroups.TryGetValue(h, out var wg)) weakHashGroups[h] = wg = [];
                    wg.Add(vf);
                }
                foreach (var (_, hashGroup) in weakHashGroups)
                    if (hashGroup.Count > 1)
                        result.Add(new DuplicateGroup(
                            hashGroup.OrderBy(f => f.FilePath).ToList(),
                            DuplicateConfidence.Exact,
                            "Identical content (hash + same size)"));
                continue;
            }

            // Compute partial hash for each file in the group
            var hashGroups = new Dictionary<string, List<VideoFile>>();

            foreach (var vf in group)
            {
                if (ct.IsCancellationRequested) break;
                progress?.Report((vf.FileName, hashed, totalToHash));

                if (!hashCache.TryGetValue(vf.FilePath, out var hash))
                {
                    hash = await ComputePartialHashAsync(vf.FilePath, ct);
                    hashCache[vf.FilePath] = hash;
                }
                hashed++;

                if (!hashGroups.TryGetValue(hash, out var hg))
                    hashGroups[hash] = hg = [];
                hg.Add(vf);
            }

            // Each hash sub-group = confirmed exact copies
            foreach (var (hash, hashGroup) in hashGroups)
            {
                if (hashGroup.Count > 1)
                    result.Add(new DuplicateGroup(
                        hashGroup.OrderBy(f => f.FilePath).ToList(),
                        DuplicateConfidence.Exact,
                        $"Identical content (partial hash + {reason})"));
                // Single-file hash groups are NOT duplicates — hash mismatch means different content
            }
        }

        return result.OrderByDescending(g => g.Confidence).ThenByDescending(g => g.Files[0].FileSizeBytes).ToList();
    }

    // ── Size bucketing ────────────────────────────────────────────────────────

    private static long SizeBucket(long bytes)
    {
        // Round to nearest 0.1% — groups files of "virtually identical" size
        if (bytes == 0) return 0;
        long step = Math.Max(1, (long)(bytes * SizeTolerance));
        return (bytes / step) * step;
    }

    // ── Metadata fingerprint ──────────────────────────────────────────────────

    private static string MetadataKey(VideoFile f)
    {
        var m = f.EmbeddedMetadata;

        // Priority: IMDB ID > TMDB ID > normalised title+year
        if (!string.IsNullOrWhiteSpace(m.ImdbId))
            return $"imdb:{m.ImdbId.Trim().ToLowerInvariant()}";

        if (!string.IsNullOrWhiteSpace(m.TmdbId))
            return $"tmdb:{m.TmdbId.Trim()}";

        // Normalise title: lowercase, strip punctuation, collapse whitespace
        var rawTitle = !string.IsNullOrWhiteSpace(m.Title) ? m.Title
                     : !string.IsNullOrWhiteSpace(f.ParsedTitle) ? f.ParsedTitle
                     : f.FileNameNoExt;

        var title = NormaliseTitle(rawTitle);
        var year  = (m.Year ?? f.ParsedYear ?? "").Trim();
        return $"title:{title}:{year}";
    }

    private static string NormaliseTitle(string s) =>
        System.Text.RegularExpressions.Regex.Replace(
            s.ToLowerInvariant().Trim(), @"[^a-z0-9]", "");

    private static (DuplicateConfidence confidence, string reason) ScoreMetadataGroup(
        string key, List<VideoFile> files)
    {
        if (key.StartsWith("imdb:") || key.StartsWith("tmdb:"))
            return (DuplicateConfidence.Strong, $"Same IMDB/TMDB ID + same size");

        // Title + year
        var sample = files[0].EmbeddedMetadata;
        bool hasYear = !string.IsNullOrWhiteSpace(sample.Year ?? files[0].ParsedYear);
        return hasYear
            ? (DuplicateConfidence.Strong,  "Same title + year + size")
            : (DuplicateConfidence.Probable,"Same title + size");
    }

    // ── Partial content hash (SHA-256 of first + last N bytes) ───────────────

    private static async Task<string> ComputePartialHashAsync(
        string filePath, CancellationToken ct)
    {
        try
        {
            using var sha = SHA256.Create();
            using var fs  = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);

            long fileSize = fs.Length;
            int  sample   = (int)Math.Min(HashSampleBytes, fileSize / 2);
            if (sample < 1) sample = (int)fileSize;

            // Read start
            var buffer = new byte[sample];
            int bytesRead = await fs.ReadAsync(buffer.AsMemory(0, sample), ct);
            sha.TransformBlock(buffer, 0, bytesRead, null, 0);

            // Append file size as 8 bytes (distinguishes files of close-but-not-equal sizes)
            var sizeBytes = BitConverter.GetBytes(fileSize);
            sha.TransformBlock(sizeBytes, 0, 8, null, 0);

            // Read end (if file is large enough to have a distinct end)
            if (fileSize > sample * 2)
            {
                fs.Seek(-sample, SeekOrigin.End);
                bytesRead = await fs.ReadAsync(buffer.AsMemory(0, sample), ct);
                sha.TransformBlock(buffer, 0, bytesRead, null, 0);
            }

            sha.TransformFinalBlock([], 0, 0);
            return Convert.ToHexString(sha.Hash!);
        }
        catch
        {
            return $"ERR:{filePath.GetHashCode()}";
        }
    }

    private static string FormatSizes(IEnumerable<VideoFile> files)
    {
        static string Fmt(long b) => b >= 1073741824L
            ? $"{b / 1073741824.0:F1} GB"
            : $"{b / 1048576.0:F0} MB";
        return string.Join(" vs ", files.Select(f => Fmt(f.FileSizeBytes)));
    }

    // ── Stage 5: Audio fingerprint deep scan ──────────────────────────────────

    public static async Task<List<DuplicateGroup>> DeepScanAsync(
        IReadOnlyList<VideoFile>                    files,
        FingerprintCacheService                     cache,
        double                                      similarityThreshold = 0.70,
        IProgress<(int pct, string status)>?        progress            = null,
        int?                                        maxParallelism      = null,
        CancellationToken                           ct                  = default)
    {
        if (!NativeLibraryExtractor.IsFpcalcAvailable())
        {
            System.Diagnostics.Debug.WriteLine(
                "[DeepScan] fpcalc.exe not found. " +
                "Download from https://github.com/acoustid/chromaprint/releases");
            return new List<DuplicateGroup>();
        }

        // Use N-1 cores so the UI thread stays responsive on small machines
        int workers = maxParallelism ?? Math.Max(1, Environment.ProcessorCount - 1);

        var eligible = files
            .Where(f => System.IO.File.Exists(f.FilePath)
                     && !cache.IsKnownNoAudio(f.FilePath))
            .GroupBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())   // dedupe — same file may be in Source + Destination
            .ToList();

        if (eligible.Count == 0) return new List<DuplicateGroup>();

        progress?.Report((0,
            $"Phase 1/2: Fingerprinting {eligible.Count} file(s) using {workers} parallel workers…"));

        // ── Phase 1: Parallel fingerprinting ──────────────────────────────────
        var fingerprints = new System.Collections.Concurrent.ConcurrentDictionary<
            string, string>(StringComparer.OrdinalIgnoreCase);
        var completed = 0;
        var saveLock  = new object();
        var lastReportPct = -1;

        var po = new ParallelOptions
        {
            MaxDegreeOfParallelism = workers,
            CancellationToken      = ct,
        };

        try
        {
            await Parallel.ForEachAsync(eligible, po, async (vf, innerCt) =>
            {
                // Try cache first — fingerprint may already exist from prior scan
                var fp = cache.TryGet(vf.FilePath);
                if (fp == null)
                {
                    fp = await ChromaprintService.ComputeFingerprintAsync(
                        vf.FilePath, maxSeconds: 120, ct: innerCt) ?? string.Empty;
                    cache.Store(vf.FilePath, string.IsNullOrEmpty(fp) ? null : fp);
                }

                if (!string.IsNullOrEmpty(fp))
                    fingerprints[vf.FilePath] = fp;

                var done = Interlocked.Increment(ref completed);

                // Throttle progress: report only when percent changes
                int pct = (int)(done * 50.0 / eligible.Count); // Phase 1 is 0-50%
                if (pct != lastReportPct)
                {
                    Interlocked.Exchange(ref lastReportPct, pct);
                    progress?.Report((pct,
                        $"Phase 1/2: Fingerprinting {done:N0}/{eligible.Count:N0} " +
                        $"({workers} workers)  ·  {System.IO.Path.GetFileName(vf.FilePath)}"));
                }

                // Persist cache every 50 files so progress isn't lost on cancel
                if (done % 50 == 0)
                    lock (saveLock) cache.SaveIfDirty();
            });
        }
        catch (OperationCanceledException)
        {
            cache.SaveIfDirty();
            return new List<DuplicateGroup>();
        }

        cache.SaveIfDirty();

        if (ct.IsCancellationRequested) return new List<DuplicateGroup>();

        // ── Phase 2: Pre-decode all fingerprints ONCE ─────────────────────────
        // Avoids redoing base64 decode + uint[] copy for every comparison
        // (saves ~27M allocations on a 5,000-file scan)
        progress?.Report((50,
            $"Phase 2/2: Decoding {fingerprints.Count:N0} fingerprints…"));

        var decoded = fingerprints
            .Select(kv => new
            {
                Path     = kv.Key,
                Fp       = ChromaprintService.DecodeFingerprintInternal(kv.Value),
            })
            .Where(x => x.Fp != null && x.Fp.Length > 0)
            .ToArray();

        if (ct.IsCancellationRequested) return new List<DuplicateGroup>();

        // Use a tolerant dictionary build — if a duplicate path somehow slips through,
        // keep the first instance instead of throwing and losing the entire scan.
        var vfByPath = new Dictionary<string, VideoFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var vf in eligible)
            vfByPath.TryAdd(vf.FilePath, vf);

        // ── Phase 3: Parallel pairwise comparison ────────────────────────────
        long totalPairs = (long)decoded.Length * (decoded.Length - 1) / 2;
        progress?.Report((55,
            $"Phase 2/2: Comparing {totalPairs:N0} pair(s) across {workers} workers…"));

        var matches = new System.Collections.Concurrent.ConcurrentBag<
            (VideoFile a, VideoFile b, double sim)>();
        long comparedPairs = 0;
        int lastCmpPct      = 55;

        try
        {
            // Outer parallelism on i; inner sequential to keep pairs unique
            Parallel.For(0, decoded.Length, po, i =>
            {
                if (ct.IsCancellationRequested) return;

                var a = decoded[i];
                for (int j = i + 1; j < decoded.Length; j++)
                {
                    if (ct.IsCancellationRequested) return;

                    var b   = decoded[j];
                    var sim = ChromaprintService.ComputeSimilarityFromDecoded(a.Fp!, b.Fp!);
                    if (sim >= similarityThreshold)
                        matches.Add((vfByPath[a.Path], vfByPath[b.Path], sim));
                }

                // Cumulative pair count for THIS i: (n-1) + (n-2) + ... in reverse order
                long doneForI = decoded.Length - 1 - i;
                long done     = Interlocked.Add(ref comparedPairs, doneForI);
                int  pct      = totalPairs > 0
                    ? 55 + (int)(done * 45 / totalPairs) // 55-100%
                    : 100;
                if (pct != lastCmpPct)
                {
                    Interlocked.Exchange(ref lastCmpPct, pct);
                    progress?.Report((pct,
                        $"Phase 2/2: Compared {done:N0}/{totalPairs:N0} pairs  ·  " +
                        $"{matches.Count} match(es) so far"));
                }
            });
        }
        catch (OperationCanceledException) { /* return what we have */ }

        // ── Phase 4: Build duplicate groups ─────────────────────────────────────
        // Each pair becomes its own group. The duplicate viewer dialog handles
        // pair-by-pair comparison so multi-file overlap is fine.
        var groups = matches
            .Select(m =>
            {
                var pair = new List<VideoFile> { m.a, m.b }
                    .OrderBy(f => f.FileSizeBytes).ToList();
                return new DuplicateGroup(pair,
                    DuplicateConfidence.AudioMatch,
                    $"Audio match ({m.sim:P0} similarity) — {FormatSizes(pair)}");
            })
            .OrderByDescending(g => g.Files.Sum(f => f.FileSizeBytes))
            .ToList();

        progress?.Report((100,
            $"✓ Deep scan complete  ·  {groups.Count:N0} audio duplicate group(s) found."));

        return groups;
    }

    // ── Shared excluded-pattern helpers (used by both Quick and Deep scan) ────

    /// <summary>
    /// Compiles the pattern string from settings into a list of regex strings.
    /// Extracted so both DetectAsync and DeepScanAsync share the same logic.
    /// </summary>
    public static List<System.Text.RegularExpressions.Regex> BuildExcludedPatterns(string patterns)
    {
        var result = new List<System.Text.RegularExpressions.Regex>();
        if (string.IsNullOrWhiteSpace(patterns)) return result;
        foreach (var raw in patterns.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var pattern = raw.Trim();
            if (string.IsNullOrEmpty(pattern)) continue;
            try
            {
                var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
                    .Replace("\\*", ".*").Replace("\\?", ".") + "$";
                result.Add(new System.Text.RegularExpressions.Regex(
                    regex, System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                           System.Text.RegularExpressions.RegexOptions.Compiled));
            }
            catch { /* malformed pattern — skip */ }
        }
        return result;
    }

    public static bool IsExcludedByPatterns(
        string filePath,
        IEnumerable<System.Text.RegularExpressions.Regex> patterns)
    {
        foreach (var rx in patterns)
            try { if (rx.IsMatch(filePath)) return true; } catch { }
        return false;
    }
}
