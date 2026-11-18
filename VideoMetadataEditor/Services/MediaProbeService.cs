using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VideoMetadataEditor.Services;

/// <summary>
/// Reads a video's stream layout and checks that a rewrite kept everything (build 173).
///
/// Every operation that rewrites a video (Fix Faststart, Remux) now probes the original
/// and the new file, and only keeps the new file if it has the same video, audio and
/// subtitle tracks (with the same languages), the same cover art, and the same duration.
/// This is what would have caught the old Fix Faststart bug, which silently dropped extra
/// audio tracks, subtitles and cover art.
///
/// Uses ffprobe (JSON, most reliable) when it's next to ffmpeg, otherwise falls back to
/// parsing "ffmpeg -i" output so existing installs are still protected. WPF-free so the
/// parsing and comparison are unit-tested.
/// </summary>
public static class MediaProbeService
{
    public enum StreamKind { Video, Cover, Audio, Subtitle, Data, Attachment, Other }

    /// <summary>
    /// One stream. <paramref name="Forced"/> and <paramref name="Title"/> (build 175) feed the
    /// Library's track columns; they're not part of the before/after rewrite check.
    /// </summary>
    public sealed record StreamInfo(StreamKind Kind, string Codec, string Language,
                                    bool Forced = false, string Title = "");

    public sealed record ProbeResult(IReadOnlyList<StreamInfo> Streams, double? DurationSeconds)
    {
        public int Count(StreamKind k) => Streams.Count(s => s.Kind == k);

        /// <summary>Short human summary, e.g. "1 video, 2 audio (eng, jpn), 1 subtitle (eng), cover art".</summary>
        public string Describe()
        {
            string Langs(StreamKind k)
            {
                var l = Streams.Where(s => s.Kind == k).Select(s => s.Language).ToList();
                return l.Count == 0 ? "" : $" ({string.Join(", ", l)})";
            }
            var parts = new List<string> { $"{Count(StreamKind.Video)} video" };
            if (Count(StreamKind.Audio) > 0)    parts.Add($"{Count(StreamKind.Audio)} audio{Langs(StreamKind.Audio)}");
            if (Count(StreamKind.Subtitle) > 0) parts.Add($"{Count(StreamKind.Subtitle)} subtitle{Langs(StreamKind.Subtitle)}");
            if (Count(StreamKind.Cover) > 0)    parts.Add("cover art");
            return string.Join(", ", parts);
        }
    }

    // ── Parsing ──────────────────────────────────────────────────────────────

    /// <summary>Parses "ffprobe -print_format json -show_streams -show_format" output.</summary>
    public static ProbeResult? ParseFfprobeJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var streams = new List<StreamInfo>();
            if (root.TryGetProperty("streams", out var arr))
            {
                foreach (var s in arr.EnumerateArray())
                {
                    var type  = s.TryGetProperty("codec_type", out var t) ? t.GetString() ?? "" : "";
                    var codec = s.TryGetProperty("codec_name", out var c) ? c.GetString() ?? "" : "";
                    var lang  = s.TryGetProperty("tags", out var tags) &&
                                (tags.TryGetProperty("language", out var l) || tags.TryGetProperty("LANGUAGE", out l))
                        ? l.GetString() ?? "" : "";
                    var hasDisp = s.TryGetProperty("disposition", out var d);
                    var cover  = hasDisp && Flag(d, "attached_pic");
                    var forced = hasDisp && Flag(d, "forced");
                    var title  = s.TryGetProperty("tags", out var tags2) &&
                                 (tags2.TryGetProperty("title", out var ti) || tags2.TryGetProperty("TITLE", out ti))
                        ? ti.GetString() ?? "" : "";
                    streams.Add(new StreamInfo(KindFrom(type, cover), codec, NormalizeLang(lang), forced, title.Trim()));
                }
            }
            double? duration = null;
            if (root.TryGetProperty("format", out var fmt) && fmt.TryGetProperty("duration", out var dur) &&
                double.TryParse(dur.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var secs))
                duration = secs;
            return new ProbeResult(streams, duration);
        }
        catch { return null; }
    }

    private static bool Flag(JsonElement disposition, string name) =>
        disposition.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.GetInt32() == 1;

    private static readonly Regex StreamLine = new(
        @"Stream #\d+:\d+(?:\[0x[0-9a-fA-F]+\])?(?:\((?<lang>[^)]+)\))?: (?<type>Video|Audio|Subtitle|Data|Attachment): (?<codec>[^\s,]+)",
        RegexOptions.CultureInvariant);

    private static readonly Regex DurationLine = new(
        @"Duration: (?<h>\d+):(?<m>\d+):(?<s>\d+(?:\.\d+)?)", RegexOptions.CultureInvariant);

    /// <summary>Parses the stream list ffmpeg prints for "ffmpeg -hide_banner -i file" (input #0 only).</summary>
    public static ProbeResult? ParseFfmpegInfo(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return null;
        var streams = new List<StreamInfo>();
        double? duration = null;
        bool inInput0 = false;

        foreach (var raw in stderr.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("Input #0", StringComparison.Ordinal)) { inInput0 = true; continue; }
            if (line.StartsWith("Input #", StringComparison.Ordinal) ||
                line.StartsWith("Output #", StringComparison.Ordinal)) { inInput0 = false; continue; }
            if (!inInput0) continue;

            var dm = DurationLine.Match(line);
            if (dm.Success && duration == null)
                duration = int.Parse(dm.Groups["h"].Value) * 3600 + int.Parse(dm.Groups["m"].Value) * 60 +
                           double.Parse(dm.Groups["s"].Value, CultureInfo.InvariantCulture);

            var sm = StreamLine.Match(line);
            if (!sm.Success) continue;
            var cover = line.Contains("(attached pic)", StringComparison.Ordinal);
            streams.Add(new StreamInfo(
                KindFrom(sm.Groups["type"].Value.ToLowerInvariant(), cover),
                sm.Groups["codec"].Value,
                NormalizeLang(sm.Groups["lang"].Success ? sm.Groups["lang"].Value : ""),
                Forced: line.Contains("(forced)", StringComparison.Ordinal)));
        }
        return streams.Count == 0 ? null : new ProbeResult(streams, duration);
    }

    private static StreamKind KindFrom(string type, bool attachedPic) => type switch
    {
        "video"      => attachedPic ? StreamKind.Cover : StreamKind.Video,
        "audio"      => StreamKind.Audio,
        "subtitle"   => StreamKind.Subtitle,
        "data"       => StreamKind.Data,
        "attachment" => StreamKind.Attachment,
        _            => StreamKind.Other
    };

    // "und" and empty both mean "not set".
    private static string NormalizeLang(string lang) =>
        string.IsNullOrWhiteSpace(lang) || lang.Equals("und", StringComparison.OrdinalIgnoreCase)
            ? "und" : lang.Trim().ToLowerInvariant();

    // ── Comparison ───────────────────────────────────────────────────────────

    /// <summary>
    /// Checks that <paramref name="after"/> kept everything that matters from
    /// <paramref name="before"/>. Returns null when it did, or a plain-English list of
    /// what's missing. Data streams (timecode etc.) are ignored — VME drops them on
    /// purpose. <paramref name="ignoreCover"/> is for remux to MKV, where MP4 cover art
    /// is deliberately not copied (it would become a second video track) and is
    /// re-embedded properly via MKVToolNix instead.
    /// </summary>
    public static string? Compare(ProbeResult before, ProbeResult after,
                                  bool ignoreCover = false, double durationToleranceSeconds = 1.0)
    {
        var problems = new List<string>();

        void CheckCount(StreamKind k, string label)
        {
            int b = before.Count(k), a = after.Count(k);
            if (a < b) problems.Add($"{label}: {b} before, {a} after");
        }

        CheckCount(StreamKind.Video, "video tracks");
        CheckCount(StreamKind.Audio, "audio tracks");
        CheckCount(StreamKind.Subtitle, "subtitle tracks");

        if (!ignoreCover && after.Count(StreamKind.Cover) < before.Count(StreamKind.Cover))
            problems.Add("cover art was lost");

        foreach (var (kind, label) in new[] { (StreamKind.Audio, "audio"), (StreamKind.Subtitle, "subtitle") })
        {
            var missing = MissingLanguages(before, after, kind);
            if (missing.Count > 0) problems.Add($"{label} language(s) missing: {string.Join(", ", missing)}");
        }

        if (before.DurationSeconds is double db && after.DurationSeconds is double da &&
            Math.Abs(db - da) > durationToleranceSeconds)
            problems.Add($"duration changed ({db:0.0}s → {da:0.0}s)");

        return problems.Count == 0 ? null : string.Join("; ", problems);
    }

    private static List<string> MissingLanguages(ProbeResult before, ProbeResult after, StreamKind kind)
    {
        var remaining = after.Streams.Where(s => s.Kind == kind).Select(s => s.Language).ToList();
        var missing = new List<string>();
        foreach (var lang in before.Streams.Where(s => s.Kind == kind).Select(s => s.Language))
        {
            if (!remaining.Remove(lang)) missing.Add(lang);
        }
        return missing;
    }

    // ── Running the probe ────────────────────────────────────────────────────

    /// <summary>ffprobe.exe next to the detected ffmpeg.exe, or on PATH; null if not found.</summary>
    public static string? FindFfprobe()
    {
        var ffmpeg = FfmpegService.Detect();
        if (string.IsNullOrEmpty(ffmpeg)) return null;

        var dir = Path.GetDirectoryName(ffmpeg);
        if (!string.IsNullOrEmpty(dir))
        {
            foreach (var name in new[] { "ffprobe.exe", "ffprobe" })
            {
                var p = Path.Combine(dir, name);
                if (File.Exists(p)) return p;
            }
            return null;
        }

        // ffmpeg was found on PATH (stored as the bare name) — check PATH for ffprobe too.
        if (_pathProbeChecked) return _pathProbe;
        _pathProbeChecked = true;
        try
        {
            using var proc = Process.Start(new ProcessStartInfo("ffprobe", "-version")
            {
                UseShellExecute = false, RedirectStandardOutput = true,
                RedirectStandardError = true, CreateNoWindow = true
            });
            proc?.WaitForExit(2000);
            if (proc?.ExitCode == 0) _pathProbe = "ffprobe";
        }
        catch { /* not on PATH */ }
        return _pathProbe;
    }

    private static bool _pathProbeChecked;
    private static string? _pathProbe;

    /// <summary>
    /// Probes a file. Null when neither ffprobe nor ffmpeg is available, or the file
    /// couldn't be read — callers treat that as "couldn't check", not as a failure.
    /// </summary>
    public static async Task<ProbeResult?> ProbeAsync(string path, CancellationToken ct = default)
    {
        var ffprobe = FindFfprobe();
        if (ffprobe != null)
        {
            var (code, stdout, _) = await RunAsync(ffprobe,
                $"-v error -print_format json -show_streams -show_format \"{path}\"", ct);
            if (code == 0)
            {
                var r = ParseFfprobeJson(stdout);
                if (r != null) return r;
            }
        }

        var ffmpeg = FfmpegService.Detect();
        if (ffmpeg == null) return null;
        // "ffmpeg -i file" with no output exits with an error but prints the stream list.
        var (_, _, stderr) = await RunAsync(ffmpeg, $"-hide_banner -i \"{path}\"", ct);
        return ParseFfmpegInfo(stderr);
    }

    private static async Task<(int code, string stdout, string stderr)> RunAsync(
        string exe, string args, CancellationToken ct)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false, RedirectStandardOutput = true,
                    RedirectStandardError = true, CreateNoWindow = true
                }
            };
            proc.Start();
            var outTask = proc.StandardOutput.ReadToEndAsync();
            var errTask = proc.StandardError.ReadToEndAsync();
            try { await proc.WaitForExitAsync(ct); }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { }
                throw;
            }
            return (proc.ExitCode, await outTask, await errTask);
        }
        catch (OperationCanceledException) { throw; }
        catch { return (-1, string.Empty, string.Empty); }
    }
}
