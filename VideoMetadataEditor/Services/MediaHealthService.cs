using System.IO;

namespace VideoMetadataEditor.Services;

/// <summary>
/// Stage 1 Media Health Check — detection & reporting only, ZERO external dependencies.
///
/// Reads the first few KB of each file to determine its TRUE container format from
/// magic bytes, then compares against the file extension and checks for common
/// compliance problems that cause metadata-embed failures, playback issues, or
/// slow seeking.
///
/// Does NOT modify files. Stage 2 (remux/repair via mkvmerge or ffmpeg) is separate.
/// </summary>
public static class MediaHealthService
{
    public enum HealthStatus { Ok, Warning, Error }

    public enum IssueType
    {
        None,
        ExtensionMismatch,   // .mp4 file that is actually Matroska, etc.
        NoFaststart,         // MP4 moov atom at end → slow streaming/seek, some taggers fail
        UnreadableHeader,    // couldn't read or recognise the container
        ZeroBytes,           // empty / truncated file
        UnknownContainer,    // recognised it's video but not a container we tag well
    }

    public record HealthResult(
        string       FilePath,
        string       FileName,
        string       Extension,        // actual file extension (lowercased, with dot)
        string       DetectedContainer,// "MP4", "Matroska", "AVI", "ASF/WMV", "QuickTime", etc.
        HealthStatus Status,
        IssueType    Issue,
        string       Detail,           // plain-English explanation
        string       SuggestedFix);    // plain-English remediation

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Analyses a single file. Fast — reads at most ~64 KB.</summary>
    public static HealthResult Analyse(string filePath)
    {
        var name = Path.GetFileName(filePath);
        var ext  = Path.GetExtension(filePath).ToLowerInvariant();

        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists)
                return Make(filePath, name, ext, "—", HealthStatus.Error,
                    IssueType.UnreadableHeader, "File not found.", "Re-scan the library.");

            if (info.Length == 0)
                return Make(filePath, name, ext, "—", HealthStatus.Error,
                    IssueType.ZeroBytes, "File is 0 bytes (empty or truncated download).",
                    "Delete and re-acquire the file.");

            if (info.Length < 1024)
                return Make(filePath, name, ext, "—", HealthStatus.Error,
                    IssueType.ZeroBytes, $"File is only {info.Length} bytes — likely truncated.",
                    "Delete and re-acquire the file.");

            // Read header for magic-byte detection
            byte[] header = new byte[64];
            using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                _ = fs.Read(header, 0, header.Length);

            var detected = DetectContainer(header);

            if (detected == null)
                return Make(filePath, name, ext, "Unknown", HealthStatus.Warning,
                    IssueType.UnknownContainer,
                    "Container format not recognised from the file header.",
                    "May still play, but metadata embedding could fail. Consider remuxing to MKV.");

            // Compare detected container to the extension
            var expectedExts = ExtensionsFor(detected);
            bool mismatch = !expectedExts.Contains(ext);

            if (mismatch)
            {
                var rightExt = expectedExts.FirstOrDefault() ?? "?";
                return Make(filePath, name, ext, detected, HealthStatus.Error,
                    IssueType.ExtensionMismatch,
                    $"File is named '{ext}' but is actually a {detected} container.",
                    $"Rename to '{rightExt}', or remux to match. Metadata writes may fail until fixed.");
            }

            // MP4 family: check faststart (moov atom position)
            if (detected == "MP4" || detected == "QuickTime")
            {
                var faststart = CheckMp4Faststart(filePath);
                if (faststart == false)
                    return Make(filePath, name, ext, detected, HealthStatus.Warning,
                        IssueType.NoFaststart,
                        "MP4 'moov' atom is at the end of the file (no faststart).",
                        "Slower seeking/streaming and some taggers fail. Remux with faststart " +
                        "(ffmpeg -movflags +faststart) to fix while keeping MP4.");
            }

            return Make(filePath, name, ext, detected, HealthStatus.Ok,
                IssueType.None, "No issues detected.", string.Empty);
        }
        catch (IOException)
        {
            return Make(filePath, name, ext, "—", HealthStatus.Warning,
                IssueType.UnreadableHeader,
                "File is locked or in use by another process.",
                "Close any player using the file and re-scan.");
        }
        catch (UnauthorizedAccessException)
        {
            return Make(filePath, name, ext, "—", HealthStatus.Warning,
                IssueType.UnreadableHeader,
                "Access denied reading the file.",
                "Check file permissions.");
        }
        catch (Exception ex)
        {
            return Make(filePath, name, ext, "—", HealthStatus.Error,
                IssueType.UnreadableHeader,
                $"Could not analyse: {ex.Message}", "Inspect the file manually.");
        }
    }

    // ── Magic-byte container detection ──────────────────────────────────────────

    private static string? DetectContainer(byte[] h)
    {
        if (h.Length < 12) return null;

        // ISO Base Media (MP4 / MOV / M4V): bytes 4-7 == 'ftyp'
        if (h[4] == 0x66 && h[5] == 0x74 && h[6] == 0x79 && h[7] == 0x70) // "ftyp"
        {
            // Brand at bytes 8-11 tells MP4 vs QuickTime
            var brand = System.Text.Encoding.ASCII.GetString(h, 8, 4);
            if (brand.StartsWith("qt")) return "QuickTime";
            return "MP4";
        }

        // Matroska / WebM: EBML header 0x1A 0x45 0xDF 0xA3
        if (h[0] == 0x1A && h[1] == 0x45 && h[2] == 0xDF && h[3] == 0xA3)
            return "Matroska";

        // AVI (RIFF....AVI ): "RIFF" + "AVI "
        if (h[0] == 0x52 && h[1] == 0x49 && h[2] == 0x46 && h[3] == 0x46 && // RIFF
            h[8] == 0x41 && h[9] == 0x56 && h[10] == 0x49 && h[11] == 0x20) // "AVI "
            return "AVI";

        // ASF / WMV: GUID 30 26 B2 75 8E 66 CF 11
        if (h[0] == 0x30 && h[1] == 0x26 && h[2] == 0xB2 && h[3] == 0x75 &&
            h[4] == 0x8E && h[5] == 0x66 && h[6] == 0xCF && h[7] == 0x11)
            return "ASF/WMV";

        // MPEG transport stream: sync byte 0x47 at start (and typically every 188 bytes)
        if (h[0] == 0x47)
            return "MPEG-TS";

        // MPEG program stream / MPEG-PS: 00 00 01 BA
        if (h[0] == 0x00 && h[1] == 0x00 && h[2] == 0x01 && h[3] == 0xBA)
            return "MPEG-PS";

        // FLV: "FLV"
        if (h[0] == 0x46 && h[1] == 0x4C && h[2] == 0x56)
            return "FLV";

        return null;
    }

    private static IReadOnlyList<string> ExtensionsFor(string container) => container switch
    {
        "MP4"        => new[] { ".mp4", ".m4v" },
        "QuickTime"  => new[] { ".mov", ".qt" },
        "Matroska"   => new[] { ".mkv", ".webm", ".mka" },
        "AVI"        => new[] { ".avi" },
        "ASF/WMV"    => new[] { ".wmv", ".asf" },
        "MPEG-TS"    => new[] { ".ts", ".m2ts", ".mts" },
        "MPEG-PS"    => new[] { ".mpg", ".mpeg", ".vob" },
        "FLV"        => new[] { ".flv" },
        _            => Array.Empty<string>(),
    };

    /// <summary>
    /// Returns true if MP4 has faststart (moov before mdat), false if not,
    /// null if it couldn't be determined. Scans top-level atoms only.
    /// </summary>
    private static bool? CheckMp4Faststart(string filePath)
    {
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var br = new BinaryReader(fs);

            long pos = 0;
            long len = fs.Length;
            // Walk top-level atoms: [4-byte big-endian size][4-byte type]
            while (pos + 8 <= len)
            {
                fs.Seek(pos, SeekOrigin.Begin);
                uint size = ReadUInt32BE(br);
                string type = new string(br.ReadChars(4));

                if (type == "moov") return true;   // moov found before mdat → faststart
                if (type == "mdat") return false;  // mdat first → no faststart

                if (size == 0) break;              // atom extends to EOF
                if (size == 1)                     // 64-bit extended size
                {
                    ulong big = ReadUInt64BE(br);
                    if (big == 0) break;
                    pos += (long)big;
                }
                else pos += size;

                if (size < 8) break;               // malformed
            }
            return null;
        }
        catch { return null; }
    }

    private static uint ReadUInt32BE(BinaryReader br)
    {
        var b = br.ReadBytes(4);
        return (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
    }

    private static ulong ReadUInt64BE(BinaryReader br)
    {
        var b = br.ReadBytes(8);
        ulong v = 0;
        for (int i = 0; i < 8; i++) v = (v << 8) | b[i];
        return v;
    }

    private static HealthResult Make(string path, string name, string ext, string container,
        HealthStatus status, IssueType issue, string detail, string fix)
        => new(path, name, ext, container, status, issue, detail, fix);
}
