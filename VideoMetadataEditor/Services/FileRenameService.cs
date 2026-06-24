using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using VideoMetadataEditor.Models;

namespace VideoMetadataEditor.Services;

public class FileRenameService
{
    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    // Token: {Name} or {Name:format}.  Conditional block: [ ... ] containing tokens/literals.
    private static readonly Regex TokenRegex = new(
        @"\{(?<name>[A-Za-z]+)(?::(?<fmt>[^}]+))?\}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Builds a new file name from a pattern.
    /// Supported tokens: {Title}, {Year}, {Genre}, {Director}, {Cast},
    ///                   {Rating}, {MPA}, {ImdbId}, {TmdbId}, {Resolution}, {Format}
    ///                   TV: {ShowTitle}, {Season}, {Episode}, {EpisodeTitle}, {Aired}
    ///
    /// Grammar extensions (Build 120):
    ///   • Format spec  {Season:00} / {Episode:000} — zero-pad width. Default pad
    ///     for Season/Episode remains 2 (D2) when no spec is given, so legacy
    ///     patterns are unchanged.
    ///   • Conditional block  [ ... ] — the entire bracketed segment (literals
    ///     included) is emitted only if EVERY token inside resolved to non-empty.
    ///     Example: "{ShowTitle}[ - {EpisodeTitle}]" drops " - " when no title.
    ///   • Multi-episode range — when <paramref name="episodeEnd"/> is supplied and
    ///     greater than meta.Episode, {Episode} renders as "01-03" (respecting pad).
    /// </summary>
    public string BuildFileName(string pattern, MovieMetadata meta, string extension,
        string resolution = "", string format = "", int? episodeEnd = null)
    {
        var name = RenderPattern(pattern, meta, resolution, format, episodeEnd);

        name = name.Trim(' ', '.', '-');
        if (string.IsNullOrWhiteSpace(name)) name = "Untitled";

        // Preserve the original extension exactly (keep .MP4 as .MP4, .mkv as .mkv)
        // Only add the dot if not already present
        var cleanExt = extension.StartsWith('.') ? extension : "." + extension;
        return $"{name}{cleanExt}";
    }

    /// <summary>Returns a preview of the new file name without renaming.</summary>
    public string Preview(string pattern, MovieMetadata meta, string extension,
        string resolution = "", string format = "", int? episodeEnd = null)
        => BuildFileName(pattern, meta, extension, resolution, format, episodeEnd);

    // ── Pattern rendering ──────────────────────────────────────────────────────

    /// <summary>
    /// Renders the pattern: walks conditional [ ] blocks first, then substitutes
    /// tokens. A bracket block is kept only if every token inside it is non-empty.
    /// Brackets are matched at a single nesting level (no nested [ ] supported —
    /// 80/20 by design).
    /// </summary>
    private string RenderPattern(string pattern, MovieMetadata meta,
        string resolution, string format, int? episodeEnd)
    {
        var sb = new StringBuilder(pattern.Length + 16);
        int i = 0;
        while (i < pattern.Length)
        {
            char c = pattern[i];
            if (c == '[')
            {
                int close = pattern.IndexOf(']', i + 1);
                if (close < 0)
                {
                    // Unbalanced '[' — treat the rest as a literal block with no closing.
                    sb.Append(SubstituteTokens(pattern[(i + 1)..], meta, resolution, format, episodeEnd, out _));
                    break;
                }
                var inner = pattern.Substring(i + 1, close - i - 1);
                var rendered = SubstituteTokens(inner, meta, resolution, format, episodeEnd,
                    out bool anyTokenEmpty);
                // Keep the block only if it had no empty token. A block with no tokens
                // at all is treated as a plain literal and always kept.
                if (!anyTokenEmpty) sb.Append(rendered);
                i = close + 1;
            }
            else
            {
                // Literal run up to the next '['
                int next = pattern.IndexOf('[', i);
                if (next < 0) next = pattern.Length;
                var segment = pattern.Substring(i, next - i);
                sb.Append(SubstituteTokens(segment, meta, resolution, format, episodeEnd, out _));
                i = next;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Substitutes every {Token} / {Token:fmt} in <paramref name="segment"/>.
    /// Sets <paramref name="anyTokenEmpty"/> true if the segment contained at least
    /// one token AND any token resolved to an empty string (used for [ ] blocks).
    /// </summary>
    private string SubstituteTokens(string segment, MovieMetadata meta,
        string resolution, string format, int? episodeEnd, out bool anyTokenEmpty)
    {
        bool sawEmpty = false;
        var result = TokenRegex.Replace(segment, m =>
        {
            var token = m.Groups["name"].Value;
            var fmt   = m.Groups["fmt"].Success ? m.Groups["fmt"].Value : null;
            var value = ResolveToken(token, fmt, meta, resolution, format, episodeEnd);
            if (string.IsNullOrEmpty(value)) sawEmpty = true;
            return value;
        });
        anyTokenEmpty = sawEmpty;
        return result;
    }

    /// <summary>Resolves a single token name (+ optional pad format) to its value.</summary>
    private string ResolveToken(string token, string? fmt, MovieMetadata meta,
        string resolution, string format, int? episodeEnd)
    {
        switch (token)
        {
            case "Title":     return Sanitize(meta.Title);
            case "Year":      return Sanitize(meta.Year);
            case "Genre":     return Sanitize(meta.Genre);
            case "Director":  return Sanitize(meta.Director);
            case "Cast":      return Sanitize(meta.Cast);
            case "Rating":
                return Sanitize(meta.Rating > 0
                    ? meta.Rating.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)
                    : string.Empty);
            case "MPA":        return Sanitize(meta.MpaRating);
            case "ImdbId":     return Sanitize(meta.ImdbId);
            case "TmdbId":     return Sanitize(meta.TmdbId);
            case "Resolution": return Sanitize(resolution);
            case "Format":     return Sanitize(format);

            case "ShowTitle":    return Sanitize(meta.ShowTitle);
            case "EpisodeTitle": return Sanitize(meta.EpisodeTitle);
            case "Aired":        return Sanitize(meta.AiredDate);

            case "Season":
                return meta.Season.HasValue ? PadNumber(meta.Season.Value, fmt) : string.Empty;

            case "Episode":
                if (!meta.Episode.HasValue) return string.Empty;
                var startEp = PadNumber(meta.Episode.Value, fmt);
                if (episodeEnd.HasValue && episodeEnd.Value > meta.Episode.Value)
                    return $"{startEp}-{PadNumber(episodeEnd.Value, fmt)}";
                return startEp;

            default:
                // Unknown token — leave the original braces untouched so it's visible.
                return fmt is null ? $"{{{token}}}" : $"{{{token}:{fmt}}}";
        }
    }

    /// <summary>
    /// Zero-pads an integer. A format like "00" / "000" sets the minimum width;
    /// when no format is given, Season/Episode default to width 2 (legacy D2).
    /// </summary>
    private static string PadNumber(int value, string? fmt)
    {
        int width = 2; // legacy default
        if (!string.IsNullOrEmpty(fmt))
        {
            // Accept "00" style (count of zeros) or a "D2"/"d3" style spec.
            if (fmt.All(ch => ch == '0'))
                width = fmt.Length;
            else if ((fmt[0] == 'D' || fmt[0] == 'd') && int.TryParse(fmt[1..], out int w))
                width = w;
        }
        return value.ToString("D" + width, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Renames the file on disk.</summary>
    public async Task<(bool success, string newPath, string error)> RenameFileAsync(
        string oldPath, string pattern, MovieMetadata meta,
        string resolution = "", string format = "", int? episodeEnd = null,
        CancellationToken ct = default)
    {
        return await Task.Run(() =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                var dir     = Path.GetDirectoryName(oldPath) ?? "";
                var ext     = Path.GetExtension(oldPath);          // preserve case: .MP4 stays .MP4
                var newName = BuildFileName(pattern, meta, ext, resolution, format, episodeEnd);
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
