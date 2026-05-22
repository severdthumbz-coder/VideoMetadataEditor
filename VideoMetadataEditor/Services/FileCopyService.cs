using System.Diagnostics;
using System.IO;
using Microsoft.Win32.SafeHandles;

namespace VideoMetadataEditor.Services;

public enum CopyEngine { Auto, FastCopy, BuiltIn }
public enum CopyMode   { Copy, Move }
public enum ConflictMode { Skip, Overwrite, OverwriteIfNewer, Rename }

// ── Per-file result returned from the built-in engine ─────────────────────────

public record FileCopyResult(
    string  SourcePath,
    string  DestPath,
    bool    Success,
    string? Error,
    long    BytesCopied,
    bool    Verified);

// ── Disk space analysis ───────────────────────────────────────────────────────

public record DiskSpaceAnalysis(
    long   RequiredBytes,
    long   AvailableBytes,
    bool   HasSufficientSpace,
    string RequiredDisplay,
    string AvailableDisplay,
    string Message);

public class FileCopyService
{
    // ── ThrottledProgress ─────────────────────────────────────────────────────
    // Caps byte-progress callbacks to maxHz/sec so the UI dispatcher
    // is never flooded during large file copies.
    private sealed class ThrottledProgress
    {
        private readonly Action<long> _inner;
        private readonly long         _minTicksGap;
        private long                  _lastTick;
        private long                  _pending;

        public ThrottledProgress(Action<long> inner, int maxHz = 20)
        {
            _inner       = inner;
            _minTicksGap = System.Diagnostics.Stopwatch.Frequency / maxHz;
        }

        public void Invoke(long bytes)
        {
            _pending += bytes;
            var now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (now - _lastTick < _minTicksGap) return;
            _lastTick = now;
            var flush = Interlocked.Exchange(ref _pending, 0);
            if (flush > 0) _inner(flush);
        }

        public void Flush()
        {
            var flush = Interlocked.Exchange(ref _pending, 0);
            if (flush > 0) _inner(flush);
        }
    }

    // ── Engine Detection ──────────────────────────────────────────────────────

    private static string? _fastCopyPath;
    private static bool    _detected;

    public static void DetectEngines()
    {
        if (_detected) return;
        _detected = true;


        // FastCopy — standard paths + user-specific non-standard locations
        var fastCopyPaths = new[]
        {
            // Standard install locations
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "FastCopy", "FastCopy.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "FastCopy", "FastCopy.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "FastCopy4", "FastCopy.exe"),
            // Non-standard: user profile root (e.g. C:\Users\ragin\FastCopy)
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "FastCopy", "FastCopy.exe"),
            // Also check adjacent version-numbered folders
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "FastCopy4", "FastCopy.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "FastCopy3", "FastCopy.exe"),
            // Hard-coded common paths
            @"C:\Program Files\FastCopy\FastCopy.exe",
            @"C:\Program Files (x86)\FastCopy\FastCopy.exe",
        };

        _fastCopyPath = fastCopyPaths.FirstOrDefault(File.Exists);

        // Fallback: walk all direct subdirs of %USERPROFILE% looking for FastCopy.exe
        if (_fastCopyPath == null)
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            try
            {
                _fastCopyPath = Directory
                    .EnumerateDirectories(userProfile)
                    .Select(d => Path.Combine(d, "FastCopy.exe"))
                    .FirstOrDefault(File.Exists);
            }
            catch { /* permission issues on some dirs */ }
        }
    }

    public static bool   FastCopyAvailable => _fastCopyPath != null;
    public static string FastCopyPath      => _fastCopyPath ?? string.Empty;

    // Large file threshold for Auto mode: files > 500 MB prefer FastCopy's raw I/O
    private const long LargeFileThreshold = 500L * 1024 * 1024;

    /// <summary>
    /// Resolves the engine to use.
    /// Auto mode: FastCopy (if installed) → Custom Fast.
    /// TeraCopy is excluded from Auto — it requires UAC elevation on every launch
    /// and has unreliable CLI behaviour when called programmatically.
    /// TeraCopy removed from active engine selection (CLI compatibility + mandatory UAC prompt).
    /// Any saved "TeraCopy" preference silently maps to FastCopy or Custom Fast.
    /// </summary>
    public static CopyEngine ResolvedEngine(string preferred, IReadOnlyList<string>? sources = null)
    {
        return preferred switch
        {
            "FastCopy"                         => FastCopyAvailable ? CopyEngine.FastCopy : CopyEngine.BuiltIn,
            "Auto"      when FastCopyAvailable => CopyEngine.FastCopy,
            _                                  => CopyEngine.BuiltIn,
        };
    }

    // ── Smart Destination Resolver ───────────────────────────────────────────────

    /// <summary>
    /// Resolves the destination directory for a single file based on content type
    /// and the Smart Organise settings. Creates the directory if needed.
    ///
    /// Rules:
    ///   Movie  → baseDestination (unchanged)
    ///   TV     → baseDestination/tvFolderName/ShowTitle (Year)/Season NN/
    ///
    /// Season subfolders are only created when <paramref name="createSeasonFolders"/> is true.
    /// If <paramref name="showTitle"/> is empty the file goes to baseDestination/tvFolderName/
    /// </summary>
    public static string ResolveDestination(
        string  baseDestination,
        bool    isEpisode,
        string  showTitle,
        string  showYear,
        int?    season,
        bool    smartOrganise,
        string  moviesFolderName    = "Movies",
        string  tvFolderName        = "TV Shows",
        bool    createSeasonFolders = true,
        string  untaggedHandling    = "SendToUnsorted",
        string  unsortedFolderName  = "Unsorted",
        string  title               = "",
        string  filePath            = "")
    {
        if (!smartOrganise)
            return baseDestination;

        // ── FILENAME-BASED TV DETECTION (untagged file rescue) ────────────────
        // If a file has no embedded metadata but its filename matches an
        // episode pattern (S01E02, 1x02, etc.), promote it to a TV episode
        // so it's routed correctly instead of dumped in Movies/Unsorted.
        if (!isEpisode
            && string.IsNullOrWhiteSpace(showTitle)
            && !string.IsNullOrWhiteSpace(filePath))
        {
            var baseName = Path.GetFileNameWithoutExtension(filePath);
            var ep       = FilenameParser.ParseEpisode(baseName);
            if (ep.HasValue && !string.IsNullOrWhiteSpace(ep.Value.showTitle))
            {
                isEpisode = true;
                showTitle = ep.Value.showTitle;
                season    = ep.Value.season;
                // title intentionally untouched — keep it empty for the TV path below
            }
        }

        // Detect untagged files: no Title AND no ShowTitle AND no episode marker.
        // Year alone isn't enough — many raw filenames yield a year via parsing
        // but have no other identification.
        bool isUntagged = !isEpisode
                       && string.IsNullOrWhiteSpace(title)
                       && string.IsNullOrWhiteSpace(showTitle);

        if (isUntagged)
        {
            switch (untaggedHandling)
            {
                case "Skip":
                    // Caller checks for a special sentinel value to skip the file
                    return string.Empty;
                case "SendToUnsorted":
                    var unsortedDir = string.IsNullOrWhiteSpace(unsortedFolderName)
                        ? baseDestination
                        : Path.Combine(baseDestination, unsortedFolderName);
                    Directory.CreateDirectory(unsortedDir);
                    return unsortedDir;
                // "SendToMovies" falls through to the regular movie path below
            }
        }

        if (!isEpisode)
        {
            // Movies go into Movies subfolder
            var movDir = string.IsNullOrWhiteSpace(moviesFolderName)
                ? baseDestination
                : Path.Combine(baseDestination, moviesFolderName);
            Directory.CreateDirectory(movDir);
            return movDir;
        }

        // Build: base/TV Shows/Show Title/Season 01/
        // NOTE: Year is intentionally excluded from the TV show folder name.
        // Using year causes folder fragmentation when different seasons or
        // episodes have different year values (e.g. Season 1 = 2020, Season 2 = 2021,
        // some files missing year entirely). Plex and Jellyfin both accept
        // "Sweet Home/Season 01/" without requiring a year suffix.
        var dir = Path.Combine(baseDestination, tvFolderName);

        if (!string.IsNullOrWhiteSpace(showTitle))
        {
            var safeTitle  = SanitizeFolderName(showTitle);
            // No year suffix — all seasons of the same show go into the same folder
            dir = Path.Combine(dir, safeTitle);

            if (createSeasonFolders && season.HasValue && season.Value > 0)
                dir = Path.Combine(dir, $"Season {season.Value:D2}");
        }

        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string SanitizeFolderName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name.Trim().TrimEnd('.');
    }

    // ── Disk Space Analysis ───────────────────────────────────────────────────

    public static DiskSpaceAnalysis AnalyseDiskSpace(
        IReadOnlyList<string> sources,
        string destinationDir)
    {
        long required = 0;
        foreach (var f in sources)
        {
            try { required += new FileInfo(f).Length; }
            catch { /* skip inaccessible */ }
        }

        long available = 0;
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(destinationDir) ?? destinationDir);
            available = drive.AvailableFreeSpace;
        }
        catch { }

        bool ok = available >= required;
        return new DiskSpaceAnalysis(
            required,
            available,
            ok,
            FormatBytes(required),
            FormatBytes(available),
            ok
                ? $"OK — {FormatBytes(available)} free, {FormatBytes(required)} needed"
                : $"INSUFFICIENT — need {FormatBytes(required)}, only {FormatBytes(available)} free");
    }

    // ── External tool dispatch ────────────────────────────────────────────────

    /// <summary>
    /// External engine result — includes per-file outcome data parsed from FastCopy log.
    /// </summary>
    public record ExternalCopyResult(
        bool    Success,
        int     FilesCopied,
        int     FilesSkipped,
        int     FilesFailed,
        long    BytesCopied,
        string? ErrorMessage);

    /// <summary>
    /// Runs TeraCopy or FastCopy and reports real-time progress by polling
    /// the destination folder every 300 ms.
    ///
    /// Progress tuple: (bytesDone, bytesTotal, filesDone, filesTotal, currentFile, pct)
    /// This gives the ViewModel real numbers — not a fake pulse.
    /// </summary>
    public static async Task<ExternalCopyResult> RunExternalAsync(
        IReadOnlyList<string> sources,
        string destination,
        CopyMode mode,
        CopyEngine engine,
        ConflictMode conflictMode = ConflictMode.Overwrite,
        IProgress<(long bytesDone, long bytesTotal, int filesDone, int filesTotal, string current, int pct)>? progress = null,
        CancellationToken ct = default)
    {
        if (sources.Count == 0)
            return new ExternalCopyResult(false, 0, 0, 0, 0, "No source files specified.");

        string exe = string.Empty, args = string.Empty;
        string? tempListFile = null;
        string? fastLogFile  = null;

        // Pre-calculate total bytes and build a filename→size lookup for progress
        // Use explicit typed list so tuple element names survive all code paths
        var sourceInfo = new System.Collections.Generic.List<(string Path, long Size)>();
        foreach (var s in sources)
        {
            try { sourceInfo.Add((s, new FileInfo(s).Length)); }
            catch { sourceInfo.Add((s, 0L)); }
        }
        long totalBytes = sourceInfo.Sum(x => x.Size);
        int  totalFiles = sources.Count;

        // Ensure destination exists so the watcher can start immediately
        Directory.CreateDirectory(destination);

        try
        {
            // TeraCopy removed — no longer a supported engine.

            if (engine == CopyEngine.FastCopy)
            {
                tempListFile = Path.Combine(Path.GetTempPath(), $"vme_fast_{Guid.NewGuid():N}.txt");
                fastLogFile  = Path.Combine(Path.GetTempPath(), $"vme_fast_{Guid.NewGuid():N}.log");
                await File.WriteAllLinesAsync(tempListFile, sources, ct);

                string op;
                if (mode == CopyMode.Move)
                    op = conflictMode == ConflictMode.Skip ? "move_noexist" : "move";
                else
                    op = conflictMode switch
                    {
                        ConflictMode.Skip             => "noexist_only",
                        ConflictMode.OverwriteIfNewer => "update",
                        ConflictMode.Rename           => "noexist_only",
                        _                             => "force_copy"
                    };

                var dest = destination.TrimEnd('\\') + '\\';
                exe  = _fastCopyPath!;

                // /wipe_del  — verify copy before deleting source (move only; safer than default delete)
                // /no_ui     — headless operation; removed in ShellExecute fallback
                // Avoid /no_confirm_del — not supported in all FastCopy versions; causes exit code -1
                var wipeDel = mode == CopyMode.Move ? " /wipe_del" : "";
                args = $"/cmd={op} /srcfile=\"{tempListFile}\" /to=\"{dest}\" " +
                       $"/logfile=\"{fastLogFile}\" /auto_close /force_start /no_ui{wipeDel}";

                Process? fcProc;
                try
                {
                    fcProc = Process.Start(new ProcessStartInfo(exe, args)
                        { UseShellExecute = false, CreateNoWindow = false });
                }
                catch
                {
                    // ShellExecute fallback — remove /no_ui so FastCopy window appears
                    var fallbackArgs = args.Replace(" /no_ui", "");
                    fcProc = Process.Start(new ProcessStartInfo(exe, fallbackArgs)
                        { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Normal });
                }
                if (fcProc == null)
                    return new ExternalCopyResult(false, 0, 0, 0, 0, "Failed to start FastCopy.");

                await MonitorDestinationAsync(fcProc, sourceInfo, destination,
                    totalBytes, totalFiles, fastLogFile, progress, ct);

                int fcExit = fcProc.ExitCode;
                fcProc.Dispose();

                int fCopied = 0, fSkipped = 0, fFailed = 0; long fBytes = 0;
                if (fastLogFile != null && File.Exists(fastLogFile))
                    try { ParseFastCopyLog(fastLogFile, out fCopied, out fSkipped, out fFailed, out fBytes); } catch { }

                progress?.Report((totalBytes, totalBytes, totalFiles, totalFiles, string.Empty, 100));
                return new ExternalCopyResult(fcExit == 0, fCopied, fSkipped, fFailed, fBytes,
                    fcExit == 0 ? null : $"FastCopy exited with code {fcExit}");
            }

            // Fallback for BuiltIn engine — caller should use BuiltInBatchCopyAsync instead,
            // but if we reach here defensively, report it as unsupported via this path.
            return new ExternalCopyResult(false, 0, 0, 0, 0,
                "Use Custom Fast engine for built-in copy operations.");
        }
        catch (OperationCanceledException)
        {
            return new ExternalCopyResult(false, 0, 0, 0, 0, "Cancelled.");
        }
        catch (Exception ex)
        {
            return new ExternalCopyResult(false, 0, 0, 0, 0, ex.Message);
        }
        finally
        {
            if (tempListFile != null) try { File.Delete(tempListFile); } catch { }
            if (fastLogFile  != null) try { File.Delete(fastLogFile);  } catch { }
        }
    }
    /// Polls the destination folder every 300 ms while <paramref name="proc"/> runs,
    /// reporting real-time byte and file progress. Works for both TeraCopy and FastCopy.
    /// </summary>
    private static async Task MonitorDestinationAsync(
        Process proc,
        IReadOnlyList<(string Path, long Size)> sourceInfo,
        string destination,
        long totalBytes,
        int totalFiles,
        string? fastLogFile,
        IProgress<(long bytesDone, long bytesTotal, int filesDone, int filesTotal, string current, int pct)>? progress,
        CancellationToken ct)
    {
        long   lastBytesDone = 0;
        int    lastFilesDone = 0;
        string lastCurrent   = string.Empty;

#if NO_WPF
        // Test/headless build (NO_WPF defined): skip the UI-thread progress timer,
        // which depends on System.Windows.Threading. Just wait for the process.
        // This path is never compiled into the real WPF app.
        _ = lastBytesDone; _ = lastFilesDone; _ = lastCurrent;
        await proc.WaitForExitAsync(ct);
#else
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };

        timer.Tick += (_, _) =>
        {
            try
            {
                long   bytesDone = 0;
                int    filesDone = 0;
                string current   = lastCurrent;

                foreach (var (srcPath, srcSize) in sourceInfo)
                {
                    var destPath = Path.Combine(destination, Path.GetFileName(srcPath));
                    if (!File.Exists(destPath)) continue;
                    long destSize = 0;
                    try { destSize = new FileInfo(destPath).Length; } catch { }
                    bytesDone += destSize;
                    if (destSize >= srcSize && srcSize > 0) filesDone++;
                    else if (destSize > 0 && destSize < srcSize)
                        current = Path.GetFileName(srcPath);
                }

                // Supplement current filename from FastCopy log if available
                if (fastLogFile != null && File.Exists(fastLogFile))
                {
                    try
                    {
                        var lines = File.ReadAllLines(fastLogFile);
                        var last  = lines.LastOrDefault(l => !string.IsNullOrWhiteSpace(l));
                        if (last != null && last.Length < 260)
                            current = Path.GetFileName(last.Trim());
                    }
                    catch { }
                }

                if (bytesDone != lastBytesDone || filesDone != lastFilesDone)
                {
                    lastBytesDone = bytesDone;
                    lastFilesDone = filesDone;
                    lastCurrent   = current;
                    int pct = totalBytes > 0 ? (int)Math.Min(99, bytesDone * 100 / totalBytes) : 0;
                    progress?.Report((bytesDone, totalBytes, filesDone, totalFiles, current, pct));
                }
            }
            catch { }
        };

        timer.Start();
        try
        {
            await proc.WaitForExitAsync(ct);
        }
        finally
        {
            timer.Stop();
            timer = null;
        }
#endif
    }

    /// <summary>Parses a FastCopy log file for summary counts.</summary>
    private static void ParseFastCopyLog(string logPath,
        out int copied, out int skipped, out int failed, out long bytesCopied)
    {
        copied = skipped = failed = 0;
        bytesCopied = 0;

        // FastCopy log lines of interest (English locale):
        //   "Total:      13 files (14.31 GB)"
        //   "Copy:       12 files (13.5 GB)"
        //   "Skip:       1 files"
        //   "Error:      0 files"
        foreach (var line in File.ReadLines(logPath))
        {
            var t = line.Trim();
            if (t.StartsWith("Copy:", StringComparison.OrdinalIgnoreCase))
                copied = ParseCount(t);
            else if (t.StartsWith("Skip:", StringComparison.OrdinalIgnoreCase))
                skipped = ParseCount(t);
            else if (t.StartsWith("Error:", StringComparison.OrdinalIgnoreCase))
                failed = ParseCount(t);
        }

        static int ParseCount(string line)
        {
            // "Copy:       12 files (13.5 GB)"
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 1 && int.TryParse(parts[1], out int n) ? n : 0;
        }
    }


    // ── Built-in Multi-Threaded High-Speed Copy Engine ────────────────────────

    // Large file threshold — above this we use parallel chunk copy instead of sequential
    private const long ParallelThreshold = 128L * 1024 * 1024; // 128 MB threshold (was 256)

    /// <summary>
    /// Returns optimal (chunkSize, seqBufferSize) for the destination drive.
    /// NVMe:      4 MB chunks  — very low latency, many parallel queues
    /// SATA SSD:  8 MB chunks  — balanced
    /// HDD:       1 MB buffer  — minimise seeks; no chunk parallelism
    /// Network:   2 MB buffer  — network packet efficiency
    /// Removable: 2 MB buffer  — USB bandwidth limited
    /// </summary>
    private static (long chunkSize, int seqBuf) GetIoSizes(string path)
    {
        try
        {
            var p = DriveCapabilityService.GetProfile(path);
            if (p.IsNetwork)   return (4L  * 1024 * 1024,  2 * 1024 * 1024);
            if (p.IsRemovable) return (4L  * 1024 * 1024,  2 * 1024 * 1024);
            if (p.IsNvme)      return (4L  * 1024 * 1024,  4 * 1024 * 1024);
            if (p.IsSsd)       return (8L  * 1024 * 1024,  4 * 1024 * 1024);
            // HDD — sequential only, large buffer
            return             (64L * 1024 * 1024,          8 * 1024 * 1024);
        }
        catch { return (8L * 1024 * 1024, 4 * 1024 * 1024); }
    }

    /// <summary>
    /// Two-level parallelism:
    ///   Level 1 — multiple files copied concurrently (workerCount = min(CPUs, 4))
    ///   Level 2 — each large file (>256 MB) is split into 64 MB chunks written
    ///             in parallel using RandomAccess (no seek contention).
    /// Small files use sequential async IO (less overhead, same throughput).
    /// Size verification after every file. Retry with back-off on failure.
    /// </summary>
    public static async Task<List<FileCopyResult>> BuiltInBatchCopyAsync(
        IReadOnlyList<string> sources,
        string destinationDir,
        CopyMode mode,
        ConflictMode conflictMode = ConflictMode.Skip,
        int maxRetries   = 3,
        int retryDelayMs = 500,
        IProgress<(int filesDone, int total, long bytesDone, long bytesTotal, string currentFile)>? progress = null,
        CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? destMap = null)
    {
        // When destMap is provided, each file goes to its own resolved destination folder
        if (destMap != null)
            foreach (var dest in destMap.Values.Distinct(StringComparer.OrdinalIgnoreCase))
                Directory.CreateDirectory(dest);
        else
            Directory.CreateDirectory(destinationDir);

        bool isNetwork = destinationDir.StartsWith(@"\\") ||
                         (Path.GetPathRoot(destinationDir) is string root &&
                          new DriveInfo(root) is { DriveType: DriveType.Network });

        // Level-1 concurrency: how many files to copy at once
        // Network: 2 — avoids saturating the link with seeks
        // Local SSD/HDD: min(CPU, 4) — keeps IO pipeline full
        // Use DriveCapabilityService for accurate per-drive concurrency recommendations.
        // NVMe: up to 16 concurrent; SATA SSD: up to 12; HDD: max 4 to avoid seek thrashing.
        var destProfile = DriveCapabilityService.GetProfile(destinationDir);
        int workerCount = destProfile.RecommendedScanWorkers;

        // For large files, halve level-1 concurrency so level-2 chunk threads have headroom
        bool anyLarge = sources.Any(s => { try { return new FileInfo(s).Length > ParallelThreshold; } catch { return false; } });
        if (anyLarge && !destProfile.IsNetwork)
            workerCount = Math.Max(1, workerCount / 2);

        long totalBytes = sources.Sum(s => { try { return new FileInfo(s).Length; } catch { return 0L; } });

        var results    = new System.Collections.Concurrent.ConcurrentBag<FileCopyResult>();
        long bytesDone = 0;
        int  filesDone = 0;
        int  total     = sources.Count;

        using var sem = new SemaphoreSlim(workerCount);

        var tasks = sources.Select(async sourcePath =>
        {
            await sem.WaitAsync(ct);
            try
            {
                var fileName    = Path.GetFileName(sourcePath);
                var fileDest    = destMap != null && destMap.TryGetValue(sourcePath, out var d)
                                    ? d : destinationDir;
                progress?.Report((filesDone, total, Interlocked.Read(ref bytesDone), totalBytes, $"Starting: {fileName}"));

                long fileSize = 0;
                try { fileSize = new FileInfo(sourcePath).Length; } catch { }

                FileCopyResult result;
                if (fileSize > ParallelThreshold && !isNetwork)
                {
                    // Level-2: parallel chunk copy for large files
                    result = await CopyLargeFileParallelAsync(
                        sourcePath, fileDest, mode, conflictMode,
                        maxRetries, retryDelayMs, fileSize,
                        bytes =>
                        {
                            Interlocked.Add(ref bytesDone, bytes);
                            progress?.Report((filesDone, total, Interlocked.Read(ref bytesDone), totalBytes, fileName));
                        }, ct);
                }
                else
                {
                    // Sequential async for small/network files
                    result = await CopySequentialAsync(
                        sourcePath, fileDest, mode, conflictMode,
                        maxRetries, retryDelayMs,
                        bytes =>
                        {
                            Interlocked.Add(ref bytesDone, bytes);
                            progress?.Report((filesDone, total, Interlocked.Read(ref bytesDone), totalBytes, fileName));
                        }, ct);
                }

                results.Add(result);
                Interlocked.Increment(ref filesDone);
                progress?.Report((filesDone, total, Interlocked.Read(ref bytesDone), totalBytes,
                    result.Success ? $"\u2713 {fileName}" : $"\u2715 {fileName}"));
            }
            finally { sem.Release(); }
        });

        await Task.WhenAll(tasks);
        return results.ToList();
    }

    // ── Level-2: Parallel chunk copy for large files ──────────────────────────
    // Allocates the dest file first with SetLength, then writes each 64 MB chunk
    // independently using RandomAccess.WriteAsync — no seek, no lock contention.

    private static async Task<FileCopyResult> CopyLargeFileParallelAsync(
        string sourcePath,
        string destinationDir,
        CopyMode mode,
        ConflictMode conflictMode,
        int maxRetries,
        int retryDelayMs,
        long fileSize,
        Action<long>? byteProgress,
        CancellationToken ct)
    {
        var destPath = Path.Combine(destinationDir, Path.GetFileName(sourcePath));
        if (string.Equals(sourcePath, destPath, StringComparison.OrdinalIgnoreCase))
            return new FileCopyResult(sourcePath, destPath, true, null, 0, true);

        FileInfo srcInfo;
        try { srcInfo = new FileInfo(sourcePath); }
        catch (Exception ex) { return new FileCopyResult(sourcePath, destPath, false, ex.Message, 0, false); }

        // Apply conflict resolution before starting
        var (resolvedDest, skip) = ResolveConflict(destPath, srcInfo, conflictMode);
        if (skip) return new FileCopyResult(sourcePath, resolvedDest, true, "Skipped (exists)", 0, true);
        destPath = resolvedDest;

        string? lastError = null;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            if (ct.IsCancellationRequested)
                return new FileCopyResult(sourcePath, destPath, false, "Cancelled", 0, false);

            try
            {
                if (File.Exists(destPath)) File.Delete(destPath);

                // Drive-adaptive chunk size
                var (chunkSize, _) = GetIoSizes(destPath);
                var destProf = DriveCapabilityService.GetProfile(destPath);

                // Build chunk list
                var chunks = new List<(long offset, int length)>();
                for (long offset = 0; offset < fileSize; offset += chunkSize)
                    chunks.Add((offset, (int)Math.Min(chunkSize, fileSize - offset)));

                // Determine level-2 chunk parallelism based on drive type
                int chunkWorkers = destProf.IsNvme  ? Math.Min(Environment.ProcessorCount, 8)
                                 : destProf.IsSsd   ? Math.Min(Environment.ProcessorCount, 4)
                                 : 1; // HDD: sequential chunks (no seek thrashing)

                // Pre-allocate destination file, then reuse the handle for chunk writes
                // srcFile MUST be disposed before File.Delete(sourcePath) on Move.
                // Windows throws IOException if you delete an open file handle.
                FileStream srcFile;
                FileStream dstFile;
                try
                {
                    srcFile = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
                        FileShare.Read, 1, FileOptions.Asynchronous);
                    dstFile = new FileStream(destPath, FileMode.Create, FileAccess.ReadWrite,
                        FileShare.None, 1, FileOptions.Asynchronous | FileOptions.WriteThrough);
                }
                catch (Exception ex)
                {
                    return new FileCopyResult(sourcePath, destPath, false, ex.Message, 0, false);
                }

                // Both streams are wrapped in a try/finally that guarantees disposal
                // even on OperationCanceledException thrown by chunk tasks.
                long written = 0;
                try
                {

                // Assign handles and pre-allocate destination
                dstFile.SetLength(fileSize);
                SafeFileHandle srcHandle = srcFile.SafeFileHandle;
                SafeFileHandle dstHandle = dstFile.SafeFileHandle;

                var chunkThrottle = byteProgress != null ? new ThrottledProgress(byteProgress, 20) : null;
                using var chunkSem = new SemaphoreSlim(chunkWorkers);

                var chunkTasks = chunks.Select(async chunk =>
                {
                    await chunkSem.WaitAsync(ct);
                    try
                    {
                        var (offset, length) = chunk;
                        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(length);
                        try
                        {
                        int read = 0;
                        while (read < length)
                        {
                            int n = await RandomAccess.ReadAsync(srcHandle,
                                buffer.AsMemory(read, length - read), offset + read, ct);
                            if (n == 0) break;
                            read += n;
                        }
                        await RandomAccess.WriteAsync(dstHandle,
                            buffer.AsMemory(0, read), offset, ct);
                        Interlocked.Add(ref written, read);
                        chunkThrottle?.Invoke(read);
                        }
                        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
                    }
                    finally { chunkSem.Release(); }
                });

                await Task.WhenAll(chunkTasks);
                chunkThrottle?.Flush();

                } // end try-for-stream-disposal
                finally
                {
                    // Guaranteed disposal on all paths including OperationCanceledException.
                    // Windows will not permit File.Delete on a file with an open handle.
                    srcFile.Dispose();
                    dstFile.Dispose();
                }

                // Timestamps (handles closed — safe to touch the file now)
                File.SetLastWriteTimeUtc(destPath, srcInfo.LastWriteTimeUtc);
                File.SetCreationTimeUtc(destPath,  srcInfo.CreationTimeUtc);

                // Verify size
                long destSize = new FileInfo(destPath).Length;
                if (destSize != fileSize)
                {
                    lastError = $"Size mismatch (src={fileSize}, dst={destSize})";
                    try { File.Delete(destPath); } catch { }
                    byteProgress?.Invoke(-written);
                    if (attempt < maxRetries) { await Task.Delay(retryDelayMs * attempt, ct); continue; }
                    return new FileCopyResult(sourcePath, destPath, false, lastError, written, false);
                }

                // Delete source — both file handles are disposed, this will now succeed
                if (mode == CopyMode.Move)
                    try { File.Delete(sourcePath); }
                    catch (Exception ex) { lastError = $"Move: copy OK but source delete failed: {ex.Message}"; }

                return new FileCopyResult(sourcePath, destPath, true, lastError, written, true);
            }
            catch (OperationCanceledException)
            {
                try { if (File.Exists(destPath)) File.Delete(destPath); } catch { }
                return new FileCopyResult(sourcePath, destPath, false, "Cancelled", 0, false);
            }
            catch (Exception ex)
            {
                lastError = $"Attempt {attempt}: {ex.Message}";
                try { if (File.Exists(destPath)) File.Delete(destPath); } catch { }
                if (attempt < maxRetries) await Task.Delay(retryDelayMs * attempt, ct);
            }
        }

        return new FileCopyResult(sourcePath, destPath, false, lastError ?? "Unknown", 0, false);
    }

    private static bool isLocalSSD(string path)
    {
        // Use DriveCapabilityService for accurate SSD vs HDD detection via IOCTL.
        // DriveType.Fixed includes spinning HDDs — this was incorrectly using 4 chunk
        // workers for HDDs, causing seek thrashing on magnetic drives.
        try { return DriveCapabilityService.GetProfile(path).IsSsd; }
        catch { return true; } // safe default: treat unknown as SSD
    }

    // ── Sequential async copy for small/network files ─────────────────────────

    private static async Task<FileCopyResult> CopySequentialAsync(
        string sourcePath,
        string destinationDir,
        CopyMode mode,
        ConflictMode conflictMode,
        int maxRetries,
        int retryDelayMs,
        Action<long>? byteProgress,
        CancellationToken ct)
    {
        const int BufferSize = 8 * 1024 * 1024; // 8 MB
        var destPath = Path.Combine(destinationDir, Path.GetFileName(sourcePath));
        if (string.Equals(sourcePath, destPath, StringComparison.OrdinalIgnoreCase))
            return new FileCopyResult(sourcePath, destPath, true, null, 0, true);

        // ── Fast-path: same-drive MOVE = O(1) atomic rename ──────────────────
        if (mode == CopyMode.Move)
        {
            try
            {
                var srcRoot  = Path.GetPathRoot(Path.GetFullPath(sourcePath));
                var destRoot = Path.GetPathRoot(Path.GetFullPath(destPath));
                if (string.Equals(srcRoot, destRoot, StringComparison.OrdinalIgnoreCase))
                {
                    // Same volume — File.Move is a rename, costs microseconds
                    long fileSize = 0;
                    try { fileSize = new FileInfo(sourcePath).Length; } catch { }

                    // Handle conflict before move
                    var srcInfo2 = new FileInfo(sourcePath);
                    var (resolvedDest2, skip2) = ResolveConflict(destPath, srcInfo2, conflictMode);
                    if (skip2) return new FileCopyResult(sourcePath, resolvedDest2, true, "Skipped (exists)", 0, true);
                    if (File.Exists(resolvedDest2)) File.Delete(resolvedDest2);

                    File.Move(sourcePath, resolvedDest2);
                    byteProgress?.Invoke(fileSize); // report full size as "transferred"
                    return new FileCopyResult(sourcePath, resolvedDest2, true, null, fileSize, true);
                }
            }
            catch (Exception ex)
            {
                // Fall through to full copy-then-delete if rename fails (e.g. cross-FS on same letter)
                _ = ex;
            }
        }

        FileInfo srcInfo;
        try { srcInfo = new FileInfo(sourcePath); }
        catch (Exception ex) { return new FileCopyResult(sourcePath, destPath, false, ex.Message, 0, false); }

        // Apply conflict resolution before copy loop
        var (resolvedDest, skip) = ResolveConflict(destPath, srcInfo, conflictMode);
        if (skip) return new FileCopyResult(sourcePath, resolvedDest, true, "Skipped (exists)", 0, true);
        destPath = resolvedDest;
        long srcSize   = srcInfo.Length;
        string? lastError = null;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            if (ct.IsCancellationRequested)
                return new FileCopyResult(sourcePath, destPath, false, "Cancelled", 0, false);

            try
            {
                long written = 0;

                if (File.Exists(destPath))
                    File.Delete(destPath);

                // Rent from ArrayPool — avoids 8MB heap allocation per file
                var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(BufferSize);
                try
                {
                using (var src = new FileStream(sourcePath, FileMode.Open, FileAccess.Read,
                           FileShare.Read, BufferSize,
                           FileOptions.SequentialScan | FileOptions.Asynchronous))
                // WriteThrough: OS flushes write buffer to disk immediately — no page cache double-copy.
                // This matches FastCopy's FILE_FLAG_NO_BUFFERING behaviour for large sequential files.
                using (var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write,
                           FileShare.None, BufferSize,
                           FileOptions.SequentialScan | FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    int read;
                    var throttle = byteProgress != null ? new ThrottledProgress(byteProgress) : null;
                    while ((read = await src.ReadAsync(buffer.AsMemory(0, BufferSize), ct)) > 0)
                    {
                        await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                        written += read;
                        throttle?.Invoke(read);
                    }
                    throttle?.Flush();
                }
                }
                finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }

                File.SetLastWriteTimeUtc(destPath, srcInfo.LastWriteTimeUtc);
                File.SetCreationTimeUtc(destPath,  srcInfo.CreationTimeUtc);

                long destSize = new FileInfo(destPath).Length;
                bool verified = destSize == srcSize;

                if (!verified)
                {
                    try { File.Delete(destPath); } catch { }
                    lastError = $"Size mismatch (src={srcSize}, dst={destSize})";
                    if (attempt < maxRetries)
                    {
                        byteProgress?.Invoke(-written);
                        await Task.Delay(retryDelayMs * attempt, ct);
                        continue;
                    }
                    return new FileCopyResult(sourcePath, destPath, false, lastError, written, false);
                }

                if (mode == CopyMode.Move)
                    try { File.Delete(sourcePath); }
                    catch (Exception ex) { lastError = $"Move: copied OK but failed to delete source: {ex.Message}"; }

                return new FileCopyResult(sourcePath, destPath, true, lastError, written, true);
            }
            catch (OperationCanceledException)
            {
                try { if (File.Exists(destPath)) File.Delete(destPath); } catch { }
                return new FileCopyResult(sourcePath, destPath, false, "Cancelled", 0, false);
            }
            catch (Exception ex)
            {
                lastError = $"Attempt {attempt}: {ex.Message}";
                try { if (File.Exists(destPath)) File.Delete(destPath); } catch { }
                if (attempt < maxRetries)
                    await Task.Delay(retryDelayMs * attempt, ct);
            }
        }

        return new FileCopyResult(sourcePath, destPath, false, lastError ?? "Unknown error", 0, false);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Applies the chosen conflict resolution policy.
    /// Returns (finalDestPath, shouldSkip).
    /// shouldSkip=true means the file should NOT be copied (Skip policy or OverwriteIfNewer when dest is newer).
    /// </summary>
    private static (string destPath, bool skip) ResolveConflict(
        string destPath, FileInfo srcInfo, ConflictMode mode)
    {
        if (!File.Exists(destPath)) return (destPath, false); // no conflict

        return mode switch
        {
            ConflictMode.Skip => (destPath, true),

            ConflictMode.Overwrite => (destPath, false), // delete handled in copy loop

            ConflictMode.OverwriteIfNewer =>
                srcInfo.LastWriteTimeUtc > new FileInfo(destPath).LastWriteTimeUtc
                    ? (destPath, false)   // source is newer — overwrite
                    : (destPath, true),   // dest is same age or newer — skip

            ConflictMode.Rename => (UniqueDestPath(destPath), false),

            _ => (destPath, false)
        };
    }

    private static string UniqueDestPath(string destPath)
    {
        if (!File.Exists(destPath)) return destPath;
        var dir  = Path.GetDirectoryName(destPath) ?? "";
        var name = Path.GetFileNameWithoutExtension(destPath);
        var ext  = Path.GetExtension(destPath);
        int n = 2;
        string candidate;
        do { candidate = Path.Combine(dir, $"{name} ({n++}){ext}"); }
        while (File.Exists(candidate));
        return candidate;
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024)            return $"{bytes} B";
        if (bytes < 1024 * 1024)     return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F2} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}
