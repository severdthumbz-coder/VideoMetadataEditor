using System.Diagnostics;
using System.IO;

namespace VideoMetadataEditor.Services;

/// <summary>
/// Detects ffmpeg.exe and provides lossless remux operations for Stage 2
/// of the Media Health Check (faststart fixes, container remux).
///
/// Detection order: native\ folder → app folder → PATH → common install dirs.
/// All operations are STREAM COPY (-c copy) — lossless and fast, never re-encode.
/// </summary>
public static class FfmpegService
{
    private static string? _exePath;
    private static bool    _detected;

    public static bool   IsAvailable => Detect() != null;
    public static string? ExePath     => Detect();

    /// <summary>Locates ffmpeg.exe, caching the result. Returns null if not found.</summary>
    public static string? Detect(bool forceRedetect = false)
    {
        if (_detected && !forceRedetect) return _exePath;
        _detected = true;

        var candidates = new List<string>
        {
            // Portable — native folder (same place as fpcalc.exe)
            Path.Combine(NativeLibraryExtractor.NativeDir, "ffmpeg.exe"),
            // Portable — same folder as the EXE
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin", "ffmpeg.exe"),
            // Common install locations
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "ffmpeg", "bin", "ffmpeg.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "scoop", "apps", "ffmpeg", "current", "bin", "ffmpeg.exe"),
            @"C:\ffmpeg\bin\ffmpeg.exe",
        };

        _exePath = candidates.FirstOrDefault(File.Exists);

        if (_exePath == null)
        {
            // Try PATH
            try
            {
                using var proc = Process.Start(new ProcessStartInfo("ffmpeg", "-version")
                {
                    UseShellExecute = false, RedirectStandardOutput = true,
                    RedirectStandardError = true, CreateNoWindow = true
                });
                proc?.WaitForExit(2000);
                if (proc?.ExitCode == 0) _exePath = "ffmpeg";
            }
            catch { /* not on PATH */ }
        }

        return _exePath;
    }

    /// <summary>Returns the ffmpeg version string, or null if unavailable.</summary>
    public static async Task<string?> GetVersionAsync()
    {
        var exe = Detect();
        if (exe == null) return null;
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo(exe, "-version")
                {
                    UseShellExecute = false, RedirectStandardOutput = true,
                    RedirectStandardError = true, CreateNoWindow = true
                }
            };
            proc.Start();
            var output = await proc.StandardOutput.ReadLineAsync() ?? string.Empty;
            await proc.WaitForExitAsync();
            // First line: "ffmpeg version 7.0.1 Copyright..."
            var parts = output.Split(' ');
            return parts.Length >= 3 ? parts[2] : output.Trim();
        }
        catch { return null; }
    }

    public record FixResult(bool Success, string Message, string? OutputPath);

    /// <summary>Marker for in-progress faststart temp files. Used by the startup sweep.</summary>
    public const string FaststartTempMarker = ".faststart.tmp";

    /// <summary>
    /// Finds orphaned *.faststart.tmp.* files left behind when a faststart fix was
    /// interrupted (app closed / crash). These are incomplete and safe to delete.
    /// </summary>
    public static List<string> FindFaststartOrphans(string folder, bool recursive)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return result;
        try
        {
            var opt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            foreach (var f in Directory.EnumerateFiles(folder, "*", opt))
            {
                var name = Path.GetFileNameWithoutExtension(f); // "Movie.faststart.tmp" or "Movie.faststart.tmp (1)"
                // Contains (not EndsWith) so collision-suffixed temps like
                // "Movie.faststart.tmp (1).mp4" are also caught.
                if (name.Contains(FaststartTempMarker, StringComparison.OrdinalIgnoreCase))
                    result.Add(f);
            }
        }
        catch { /* permission / IO */ }
        return result;
    }

    /// <summary>
    /// Adds faststart to an MP4 in place (moov atom moved to front) via lossless remux.
    ///
    /// Build 168 rewrite — the old version could lose data:
    ///  • It ran "-c copy" WITHOUT "-map 0", so ffmpeg kept only one video, one audio and
    ///    one subtitle stream. Embedded cover art (an attached-picture stream), extra audio
    ///    languages and extra subtitle tracks were silently dropped.
    ///  • It deleted the original BEFORE moving the temp into place, and if that move failed
    ///    it deleted the temp too — the only remaining copy.
    ///
    /// Now: every stream is kept (-map 0, data streams excluded), the output is checked
    /// (size and moov-first) before it's trusted, and the original is replaced in a single
    /// overwrite-move, so at every moment at least one complete copy exists. The temp is
    /// only deleted when the original is confirmed still present.
    /// </summary>
    public static async Task<FixResult> AddFaststartAsync(
        string filePath, CancellationToken ct = default)
    {
        var exe = Detect();
        if (exe == null)
            return new FixResult(false, "ffmpeg.exe not found.", null);
        if (!File.Exists(filePath))
            return new FixResult(false, "Source file not found.", null);

        var dir     = Path.GetDirectoryName(filePath)!;
        var tempOut = Path.Combine(dir,
            Path.GetFileNameWithoutExtension(filePath) + FaststartTempMarker + Path.GetExtension(filePath));
        // Avoid colliding with an orphan temp from a prior interrupted run
        int tn = 1;
        while (File.Exists(tempOut))
            tempOut = Path.Combine(dir,
                $"{Path.GetFileNameWithoutExtension(filePath)}{FaststartTempMarker} ({tn++}){Path.GetExtension(filePath)}");

        var originalSize = new FileInfo(filePath).Length;

        // Build 173: record exactly what the original contains, to compare afterwards.
        MediaProbeService.ProbeResult? before;
        try { before = await MediaProbeService.ProbeAsync(filePath, ct); }
        catch (OperationCanceledException) { return new FixResult(false, "Cancelled.", null); }
        LogProbe("Faststart — streams before", before, filePath);

        // -map 0      keep EVERY stream: all video incl. cover art, all audio, all subtitles
        // -dn         except data streams (timecode etc.), which the MP4 muxer often rejects
        // -c copy     stream copy — lossless, no re-encode
        // -map_metadata 0 / -map_chapters 0   keep tags and chapters
        // +faststart  move the moov atom to the front
        var args = $"-y -i \"{filePath}\" -map 0 -dn -c copy -map_metadata 0 -map_chapters 0 " +
                   $"-movflags +faststart \"{tempOut}\"";

        var (ok, err) = await RunAsync(exe, args, ct);
        if (!ok || !File.Exists(tempOut))
        {
            TryDelete(tempOut);   // original untouched — safe to discard the temp
            return new FixResult(false, $"ffmpeg failed: {err}", null);
        }

        // ── Verify before trusting the new file ──────────────────────────────
        var newSize = new FileInfo(tempOut).Length;
        if (newSize < originalSize * 0.95)
        {
            TryDelete(tempOut);
            return new FixResult(false,
                $"Output was {newSize:N0} bytes vs {originalSize:N0} original — more than 5% smaller, " +
                "so streams may have been lost. Original left unchanged.", null);
        }
        if (MediaHealthService.CheckMp4Faststart(tempOut) != true)
        {
            TryDelete(tempOut);
            return new FixResult(false, "Output didn't come out as faststart. Original left unchanged.", null);
        }

        // Build 173: stream check — same video/audio/subtitle tracks, languages, cover
        // art and duration as the original, or the fixed copy is discarded.
        string verifiedNote;
        if (before != null)
        {
            MediaProbeService.ProbeResult? after;
            try { after = await MediaProbeService.ProbeAsync(tempOut, ct); }
            catch (OperationCanceledException)
            {
                TryDelete(tempOut);   // original untouched
                return new FixResult(false, "Cancelled.", null);
            }
            if (after == null)
            {
                TryDelete(tempOut);
                return new FixResult(false, "Couldn't read the fixed copy to verify it. Original left unchanged.", null);
            }
            LogProbe("Faststart — streams after", after, tempOut);
            var problem = MediaProbeService.Compare(before, after);
            if (problem != null)
            {
                TryDelete(tempOut);
                return new FixResult(false, $"Verification failed — {problem}. Original left unchanged.", null);
            }
            verifiedNote = $"verified: {after.Describe()}";
        }
        else
        {
            verifiedNote = "stream check unavailable";
        }

        // ── Replace: one overwrite-move. Either it succeeds, or the original stays. ──
        var origWrite = File.GetLastWriteTimeUtc(filePath);
        Exception? last = null;
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                File.Move(tempOut, filePath, overwrite: true);
                try { File.SetLastWriteTimeUtc(filePath, origWrite); } catch { /* cosmetic */ }
                OperationJournal.Note("Faststart", filePath, filePath);   // build 174: history (record only)
                return new FixResult(true, $"Faststart added (lossless, {verifiedNote}).", filePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Usually a brief lock (Plex, antivirus, Explorer preview). Wait and retry.
                last = ex;
                if (attempt < 5) await Task.Delay(1000 * attempt, CancellationToken.None);
            }
        }

        // Couldn't replace. Only discard the temp if the original is definitely still there;
        // otherwise the temp IS the file and must be kept (startup recovery will offer to restore it).
        if (File.Exists(filePath) && new FileInfo(filePath).Length > 0)
        {
            TryDelete(tempOut);
            return new FixResult(false,
                $"Couldn't replace the original (in use?): {last?.Message} Original left unchanged.", null);
        }
        return new FixResult(false,
            $"Couldn't replace the original: {last?.Message} The fixed copy was kept as " +
            $"\"{Path.GetFileName(tempOut)}\" — rename it to \"{Path.GetFileName(filePath)}\".", tempOut);
    }

    /// <summary>
    /// Remuxes a file to a new container (lossless stream copy).
    /// targetExt e.g. ".mkv" or ".mp4". Output is a NEW candidate file alongside the
    /// source, named "<stem>.remux<targetExt>" so the non-destructive commit workflow
    /// (RemuxCommitService) can later adopt or discard it. Original is never touched.
    /// </summary>
    public static async Task<FixResult> RemuxAsync(
        string filePath, string targetExt, CancellationToken ct = default)
    {
        var exe = Detect();
        if (exe == null)
            return new FixResult(false, "ffmpeg.exe not found.", null);
        if (!File.Exists(filePath))
            return new FixResult(false, "Source file not found.", null);

        var dir  = Path.GetDirectoryName(filePath)!;
        var stem = Path.GetFileNameWithoutExtension(filePath);
        // Candidate marker keeps it visibly temporary and sweepable on next launch
        var outPath = Path.Combine(dir, stem + RemuxCommitService.Marker + targetExt);

        // Avoid clobber if a prior candidate exists
        int n = 1;
        while (File.Exists(outPath))
            outPath = Path.Combine(dir,
                $"{stem}{RemuxCommitService.Marker} ({n++}){targetExt}");

        // Build 173: probe first — decides the stream mapping and is the baseline for
        // the after-check.
        MediaProbeService.ProbeResult? before;
        try { before = await MediaProbeService.ProbeAsync(filePath, ct); }
        catch (OperationCanceledException) { return new FixResult(false, "Cancelled.", null); }
        LogProbe("Remux — streams before", before, filePath);
        bool toMkv = targetExt.Equals(".mkv", StringComparison.OrdinalIgnoreCase);
        bool toMp4 = targetExt.Equals(".mp4", StringComparison.OrdinalIgnoreCase);

        string args;
        if (toMkv)
        {
            // MKV holds every real track. "0:V" (capital V) = video tracks that aren't cover
            // pictures — copying an MP4 cover into MKV turns it into a bogus second video
            // track, so it's left out (re-embed artwork afterwards; MKV covers go in via
            // MKVToolNix). MP4 text subtitles (mov_text) can't be stored in MKV, so they're
            // converted to SRT. Build 168's plain "-map 0" made these remuxes fail outright.
            bool hasMovText = before?.Streams.Any(s =>
                                  s.Kind == MediaProbeService.StreamKind.Subtitle &&
                                  s.Codec.Equals("mov_text", StringComparison.OrdinalIgnoreCase))
                              ?? Path.GetExtension(filePath).ToLowerInvariant() is ".mp4" or ".m4v" or ".mov";
            args = $"-y -i \"{filePath}\" -map 0:V -map 0:a? -map 0:s? -map 0:t? -dn -c copy " +
                   (hasMovText ? "-c:s srt " : string.Empty) +
                   $"-map_metadata 0 -map_chapters 0 \"{outPath}\"";
        }
        else
        {
            // MP4 output keeps ffmpeg's default selection, because image-based subtitles from
            // MKV can't go into MP4. The after-check below reports anything not carried over.
            var extra = toMp4 ? "-movflags +faststart " : string.Empty;
            args = $"-y -i \"{filePath}\" -c copy {extra}\"{outPath}\"";
        }

        var (ok, err) = await RunAsync(exe, args, ct);
        if (!ok || !File.Exists(outPath))
        {
            TryDelete(outPath);
            return new FixResult(false, $"ffmpeg failed: {err}", null);
        }

        // ── After-check ─────────────────────────────────────────────────────
        var note = "stream check unavailable";
        if (before != null)
        {
            MediaProbeService.ProbeResult? after;
            try { after = await MediaProbeService.ProbeAsync(outPath, ct); }
            catch (OperationCanceledException)
            {
                TryDelete(outPath);
                return new FixResult(false, "Cancelled.", null);
            }
            LogProbe("Remux — streams after", after, outPath);
            var problem = after == null ? "couldn't read the new file"
                                        : MediaProbeService.Compare(before, after, ignoreCover: toMkv);
            if (problem != null && toMkv)
            {
                // MKV is meant to keep everything — a shortfall means something went wrong.
                TryDelete(outPath);
                return new FixResult(false, $"Verification failed — {problem}. Nothing was changed.", null);
            }
            note = problem == null
                ? $"verified: {after!.Describe()}"
                : $"⚠ not everything carried over: {problem}. Review before replacing the original";
            if (toMkv && before.Count(MediaProbeService.StreamKind.Cover) > 0)
                note += ". Cover art isn't copied into MKV by remux — re-embed artwork afterwards";
        }
        OperationJournal.Note("Remux", filePath, outPath);   // build 174: history (record only)
        return new FixResult(true, $"Remuxed to {Path.GetFileName(outPath)} (lossless; {note}).", outPath);
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    private static async Task<(bool ok, string err)> RunAsync(
        string exe, string args, CancellationToken ct)
    {
        Process? proc = null;
        var sw = Stopwatch.StartNew();
        var command = $"{Path.GetFileName(exe)} {args}";
        try
        {
            proc = new Process
            {
                StartInfo = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false, RedirectStandardOutput = true,
                    RedirectStandardError = true, CreateNoWindow = true
                }
            };
            proc.Start();
            var stderrTask = proc.StandardError.ReadToEndAsync();
            _ = proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync(ct);
            var stderr = await stderrTask;
            sw.Stop();
            // Build 175: every ffmpeg run is in the activity log with its full command line,
            // exit code and time; a failure carries the end of ffmpeg's output.
            if (proc.ExitCode == 0)
                LogService.Write(LogLevel.Debug, "FFmpeg", "ffmpeg finished (exit 0)", command, null, sw.Elapsed);
            else
                LogService.Write(LogLevel.Error, "FFmpeg", $"ffmpeg failed (exit {proc.ExitCode}): {LastLine(stderr)}",
                    command + Environment.NewLine + Environment.NewLine + Tail(stderr, 25), null, sw.Elapsed);
            // ffmpeg writes normal progress to stderr; only treat as error on non-zero exit
            return (proc.ExitCode == 0,
                proc.ExitCode == 0 ? string.Empty : LastLine(stderr));
        }
        catch (OperationCanceledException)
        {
            // Build 169: actually stop ffmpeg. Before, Cancel only stopped WAITING — ffmpeg
            // kept running and kept the temp file open, so it couldn't be cleaned up.
            try
            {
                if (proc is { HasExited: false })
                {
                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(5000);
                }
            }
            catch { /* already gone */ }
            LogService.Write(LogLevel.Warning, "FFmpeg", "ffmpeg stopped (cancelled)", command, null, sw.Elapsed);
            return (false, "Cancelled.");
        }
        catch (Exception ex)
        {
            LogService.Write(LogLevel.Error, "FFmpeg", $"ffmpeg couldn't run: {ex.Message}",
                command + Environment.NewLine + ex, null, sw.Elapsed);
            return (false, ex.Message);
        }
        finally { proc?.Dispose(); }
    }

    private static string Tail(string s, int lines)
    {
        var all = s.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(Environment.NewLine, all.Skip(Math.Max(0, all.Length - lines)));
    }

    private static void LogProbe(string what, MediaProbeService.ProbeResult? probe, string path) =>
        LogService.Write(LogLevel.Debug, "FFmpeg",
            probe == null ? $"{what}: couldn't read (no ffprobe, or unreadable)" : $"{what}: {probe.Describe()}",
            null, path);

    private static string LastLine(string s)
    {
        var lines = s.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return lines.Length > 0 ? lines[^1].Trim() : "unknown error";
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
