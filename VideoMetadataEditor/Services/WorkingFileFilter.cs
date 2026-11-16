using System.IO;
using System.Text.RegularExpressions;

namespace VideoMetadataEditor.Services;

/// <summary>
/// Recognises VME's own temporary working files so they are never treated as library
/// videos (build 168).
///
/// Before this, the folder watcher and library scan only skipped ".vme_*" names. A
/// Health Check faststart fix writes "&lt;name&gt;.faststart.tmp.mp4" next to the episode,
/// so the watcher picked it up as a "new" video, wrote a "&lt;name&gt;.faststart.tmp.nfo"
/// for it, and the Library counted it as an extra episode.
/// </summary>
public static class WorkingFileFilter
{
    // "Movie.remux.mkv", "Movie.remux (2).mkv" — the stem ENDS with the marker, so a real
    // title that merely contains "remux" somewhere else is not caught.
    private static readonly Regex RemuxStem =
        new(@"\.remux( \(\d+\))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>True for any VME temp / candidate / backup file (any extension).</summary>
    public static bool IsWorkingFile(string path)
    {
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name)) return false;
        if (name.StartsWith(".vme_", StringComparison.OrdinalIgnoreCase)) return true;

        var stem = Path.GetFileNameWithoutExtension(name);
        if (stem.Contains(".faststart.tmp", StringComparison.OrdinalIgnoreCase)) return true;
        return RemuxStem.IsMatch(stem);
    }

    /// <summary>
    /// For a faststart temp video, the path of the original it was made from
    /// ("Show - S01E09.faststart.tmp.mp4" → "Show - S01E09.mp4"), or null if the
    /// path isn't a faststart temp.
    /// </summary>
    public static string? FaststartOriginalFor(string tempPath)
    {
        var dir  = Path.GetDirectoryName(tempPath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(tempPath);
        var m = Regex.Match(stem, @"^(?<orig>.+?)\.faststart\.tmp( \(\d+\))?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return m.Success ? Path.Combine(dir, m.Groups["orig"].Value + Path.GetExtension(tempPath)) : null;
    }
}
