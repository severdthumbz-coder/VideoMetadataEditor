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

        // ── Replace: one overwrite-move. Either it succeeds, or the original stays. ──
        var origWrite = File.GetLastWriteTimeUtc(filePath);
        Exception? last = null;
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                File.Move(tempOut, filePath, overwrite: true);
                try { File.SetLastWriteTimeUtc(filePath, origWrite); } catch { /* cosmetic */ }
                return new FixResult(true, "Faststart added (lossless remux, all streams kept).", filePath);
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

        // For MP4 output, also apply faststart while we're remuxing
        var extra = targetExt.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
            ? "-movflags +faststart " : string.Empty;
        // Build 168: MKV can hold any stream, so keep them all (every audio/subtitle track,
        // attachments). MP4 output keeps ffmpeg's default selection, because image-based
        // subtitles from MKV can't go into MP4 — the result is a candidate the user reviews
        // before it replaces anything.
        var mapAll = targetExt.Equals(".mkv", StringComparison.OrdinalIgnoreCase)
            ? "-map 0 -dn -map_metadata 0 -map_chapters 0 " : string.Empty;
        var args = $"-y -i \"{filePath}\" {mapAll}-c copy {extra}\"{outPath}\"";

        var (ok, err) = await RunAsync(exe, args, ct);
        if (!ok || !File.Exists(outPath))
        {
            TryDelete(outPath);
            return new FixResult(false, $"ffmpeg failed: {err}", null);
        }
        return new FixResult(true, $"Remuxed to {Path.GetFileName(outPath)} (lossless).", outPath);
    }

    // ── Internal ──────────────────────────────────────────────────────────────

    private static async Task<(bool ok, string err)> RunAsync(
        string exe, string args, CancellationToken ct)
    {
        Process? proc = null;
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
            return (false, "Cancelled.");
        }
        catch (Exception ex) { return (false, ex.Message); }
        finally { proc?.Dispose(); }
    }

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
