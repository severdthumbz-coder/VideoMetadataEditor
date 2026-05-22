using System.IO;
using System.Text.RegularExpressions;
using VideoMetadataEditor.Models;

namespace VideoMetadataEditor.Services;

public class FileRenameService
{
    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    /// <summary>
    /// Builds a new file name from a pattern.
    /// Supported tokens: {Title}, {Year}, {Genre}, {Director}, {Cast},
    ///                   {Rating}, {MPA}, {ImdbId}, {TmdbId}, {Resolution}, {Format}
    ///                   TV: {ShowTitle}, {Season}, {Episode}, {EpisodeTitle}, {Aired}
    /// </summary>
    public string BuildFileName(string pattern, MovieMetadata meta, string extension,
        string resolution = "", string format = "")
    {
        var ratingStr = meta.Rating > 0
            ? meta.Rating.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
            : string.Empty;
        var name = pattern
            .Replace("{Title}",    Sanitize(meta.Title))
            .Replace("{Year}",     Sanitize(meta.Year))
            .Replace("{Genre}",    Sanitize(meta.Genre))
            .Replace("{Director}", Sanitize(meta.Director))
            .Replace("{Cast}",     Sanitize(meta.Cast))
            .Replace("{Rating}",   Sanitize(ratingStr))
            .Replace("{MPA}",      Sanitize(meta.MpaRating))
            .Replace("{ImdbId}",   Sanitize(meta.ImdbId))
            .Replace("{TmdbId}",     Sanitize(meta.TmdbId))
            .Replace("{Resolution}", Sanitize(resolution))
            .Replace("{Format}",     Sanitize(format))
            // TV / Episode tokens
            .Replace("{ShowTitle}",    Sanitize(meta.ShowTitle))
            .Replace("{Season}",       meta.Season.HasValue  ? meta.Season.Value.ToString("D2") : string.Empty)
            .Replace("{Episode}",      meta.Episode.HasValue ? meta.Episode.Value.ToString("D2") : string.Empty)
            .Replace("{EpisodeTitle}", Sanitize(meta.EpisodeTitle))
            .Replace("{Aired}",        Sanitize(meta.AiredDate));

        name = name.Trim(' ', '.', '-');
        if (string.IsNullOrWhiteSpace(name)) name = "Untitled";

        // Preserve the original extension exactly (keep .MP4 as .MP4, .mkv as .mkv)
        // Only add the dot if not already present
        var cleanExt = extension.StartsWith('.') ? extension : "." + extension;
        return $"{name}{cleanExt}";
    }

    /// <summary>Returns a preview of the new file name without renaming.</summary>
    public string Preview(string pattern, MovieMetadata meta, string extension,
        string resolution = "", string format = "")
        => BuildFileName(pattern, meta, extension, resolution, format);

    /// <summary>Renames the file on disk.</summary>
    public async Task<(bool success, string newPath, string error)> RenameFileAsync(
        string oldPath, string pattern, MovieMetadata meta,
        string resolution = "", string format = "",
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                var dir     = Path.GetDirectoryName(oldPath) ?? "";
                var ext     = Path.GetExtension(oldPath);          // preserve case: .MP4 stays .MP4
                var newName = BuildFileName(pattern, meta, ext, resolution, format);
                var newPath = Path.Combine(dir, newName);

                // Exact same path — nothing to do
                if (string.Equals(oldPath, newPath, StringComparison.Ordinal))
                    return (true, oldPath, "");

                // Case-only rename on Windows (NTFS is case-insensitive but case-preserving)
                // File.Move("a.mp4", "A.mp4") is a no-op on NTFS — use a two-step via temp
                bool caseOnlyChange = string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase);
                if (caseOnlyChange)
                {
                    var tempRename = Path.Combine(dir, $".vme_ren_{Guid.NewGuid():N}{ext}");
                    File.Move(oldPath, tempRename);
                    File.Move(tempRename, newPath);
                    return (true, newPath, "");
                }

                // Handle conflicts with other files
                if (File.Exists(newPath))
                {
                    var baseName = Path.GetFileNameWithoutExtension(newName);
                    var counter  = 1;
                    do
                    {
                        newPath = Path.Combine(dir, $"{baseName} ({counter++}){ext}");
                    } while (File.Exists(newPath));
                }

                File.Move(oldPath, newPath);
                return (true, newPath, "");
            }
            catch (OperationCanceledException)
            {
                return (false, oldPath, "Cancelled.");
            }
            catch (Exception ex)
            {
                return (false, oldPath, ex.Message);
            }
        }, ct);
    }

    private static string Sanitize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        // ── Colon handling ────────────────────────────────────────────────────
        // Titles like "The Lost Village: Beyond the Broken Road" should become
        // "The Lost Village - Beyond the Broken Road", not "The Lost Village_ …"
        // Replace ": " (colon + space) with " - " first, then bare ":" with " - "
        // so the subtitle separator is preserved legibly.
        var s = input
            .Replace(": ", " - ")   // "Title: Subtitle"  → "Title - Subtitle"
            .Replace(":", " - ");   // "Title:Subtitle"   → "Title - Subtitle"

        // ── Strip remaining invalid filename chars ────────────────────────────
        // Characters: / \ | ? * " < > are dropped outright.
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (!InvalidChars.Contains(c))
                sb.Append(c);
        }

        // Collapse whitespace runs that may result (e.g. "  -  " → " - ")
        var result = Regex.Replace(sb.ToString(), @"  +", " ").Trim();

        // Clean up any orphaned " - " at start/end
        result = result.TrimStart('-', ' ').TrimEnd('-', ' ').Trim();

        return result;
    }
}
