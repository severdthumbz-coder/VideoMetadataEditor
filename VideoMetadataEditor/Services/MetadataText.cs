using System.Text;
using VideoMetadataEditor.Models;

namespace VideoMetadataEditor.Services;

/// <summary>
/// Cleans up the whitespace in metadata text before it's written (build 177).
///
/// Lookup results sometimes carry stray whitespace — TMDB returned "One Last Shot " with a
/// trailing space. MP4 tags store it trimmed, so the post-write check read back
/// "One Last Shot", decided the write had failed, and VME retried, fell back to an
/// in-place write and finally remuxed a file that was fine. Text is now normalised once,
/// before writing, so what's written is exactly what's checked (and the filename built
/// from it has no stray spaces either). WPF-free so it's unit-tested.
/// </summary>
public static class MetadataText
{
    /// <summary>
    /// One-line field (title, genre, names, IDs): trimmed, any run of whitespace (including
    /// tabs, line breaks and non-breaking spaces) becomes one space, invisible characters
    /// (zero-width spaces, BOM, control characters) are removed.
    /// </summary>
    public static string CleanLine(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var sb = new StringBuilder(s.Length);
        bool pendingSpace = false;
        foreach (var c in s)
        {
            if (IsInvisible(c)) continue;
            if (char.IsWhiteSpace(c)) { pendingSpace = sb.Length > 0; continue; }
            if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Multi-line field (synopsis): each line cleaned like <see cref="CleanLine"/>, paragraph
    /// breaks kept (at most one blank line in a row), no blank lines at the start or end.
    /// </summary>
    public static string CleanBlock(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var lines = s.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(CleanLine).ToList();
        var outLines = new List<string>();
        foreach (var line in lines)
        {
            if (line.Length == 0 && (outLines.Count == 0 || outLines[^1].Length == 0)) continue;
            outLines.Add(line);
        }
        while (outLines.Count > 0 && outLines[^1].Length == 0) outLines.RemoveAt(outLines.Count - 1);
        return string.Join(Environment.NewLine, outLines);
    }

    private static bool IsInvisible(char c) =>
        c is '​' or '‌' or '‍' or '⁠' or '﻿' ||
        (char.IsControl(c) && !char.IsWhiteSpace(c));

    /// <summary>
    /// Cleans every text field of <paramref name="m"/> in place. Returns the names of the
    /// fields that changed (empty when nothing did), so the caller can log it.
    /// </summary>
    public static List<string> Normalize(MovieMetadata m)
    {
        var changed = new List<string>();

        string Fix(string name, string? current, Func<string?, string> clean)
        {
            var cleaned = clean(current);
            if (!string.Equals(cleaned, current ?? string.Empty, StringComparison.Ordinal)) changed.Add(name);
            return cleaned;
        }

        var title = Fix(nameof(m.Title), m.Title, CleanLine);               if (title != m.Title) m.Title = title;
        var year  = Fix(nameof(m.Year), m.Year, CleanLine);                 if (year != m.Year) m.Year = year;
        var genre = Fix(nameof(m.Genre), m.Genre, CleanLine);               if (genre != m.Genre) m.Genre = genre;
        var dir   = Fix(nameof(m.Director), m.Director, CleanLine);         if (dir != m.Director) m.Director = dir;
        var cast  = Fix(nameof(m.Cast), m.Cast, CleanLine);                 if (cast != m.Cast) m.Cast = cast;
        var desc  = Fix(nameof(m.Description), m.Description, CleanBlock);  if (desc != m.Description) m.Description = desc;
        var imdb  = Fix(nameof(m.ImdbId), m.ImdbId, CleanLine);             if (imdb != m.ImdbId) m.ImdbId = imdb;
        var tmdb  = Fix(nameof(m.TmdbId), m.TmdbId, CleanLine);             if (tmdb != m.TmdbId) m.TmdbId = tmdb;
        var mpa   = Fix(nameof(m.MpaRating), m.MpaRating, CleanLine);       if (mpa != m.MpaRating) m.MpaRating = mpa;
        var show  = Fix(nameof(m.ShowTitle), m.ShowTitle, CleanLine);       if (show != m.ShowTitle) m.ShowTitle = show;
        var ept   = Fix(nameof(m.EpisodeTitle), m.EpisodeTitle, CleanLine); if (ept != m.EpisodeTitle) m.EpisodeTitle = ept;
        var aired = Fix(nameof(m.AiredDate), m.AiredDate, CleanLine);       if (aired != m.AiredDate) m.AiredDate = aired;
        var tvdb  = Fix(nameof(m.TvdbId), m.TvdbId, CleanLine);             if (tvdb != m.TvdbId) m.TvdbId = tvdb;
        var sid   = Fix(nameof(m.TmdbSeriesId), m.TmdbSeriesId, CleanLine); if (sid != m.TmdbSeriesId) m.TmdbSeriesId = sid;
        var ani   = Fix(nameof(m.AniListId), m.AniListId, CleanLine);       if (ani != m.AniListId) m.AniListId = ani;
        return changed;
    }

    /// <summary>
    /// True when two values differ only in whitespace or letter case — what the post-write
    /// check should accept, since some containers trim what they store.
    /// </summary>
    public static bool SameText(string? a, string? b) =>
        string.Equals(CleanLine(a), CleanLine(b), StringComparison.OrdinalIgnoreCase);
}
