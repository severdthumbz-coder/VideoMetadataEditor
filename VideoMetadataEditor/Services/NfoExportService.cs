using System.IO;
using System.Text;
using VideoMetadataEditor.Models;

namespace VideoMetadataEditor.Services;

/// <summary>
/// Writes Jellyfin / Kodi / Plex .nfo sidecar files next to each video.
/// Format: Kodi XML (movie.nfo), compatible with all major media servers.
/// </summary>
public static class NfoExportService
{
    public static async Task<(int ok, int failed)> ExportAsync(
        IReadOnlyList<(string filePath, MovieMetadata meta)> files,
        IProgress<(int done, int total)>? progress = null,
        CancellationToken ct = default)
    {
        int ok = 0, failed = 0;
        int total = files.Count;

        for (int i = 0; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (filePath, meta) = files[i];
            try
            {
                var nfoPath = Path.ChangeExtension(filePath, ".nfo");
                var xml = BuildNfo(meta);
                await File.WriteAllTextAsync(nfoPath, xml, Encoding.UTF8, ct);
                ok++;
            }
            catch (OperationCanceledException) { throw; }
            catch { failed++; }

            progress?.Report((i + 1, total));
        }

        return (ok, failed);
    }

    private static string BuildNfo(MovieMetadata m)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");

        if (m.IsEpisode)
        {
            sb.AppendLine("<episodedetails>");
            AppendTag(sb, "title",     string.IsNullOrWhiteSpace(m.EpisodeTitle) ? m.Title : m.EpisodeTitle);
            AppendTag(sb, "showtitle", m.ShowTitle);
            AppendTag(sb, "season",    m.Season?.ToString() ?? "");
            AppendTag(sb, "episode",   m.Episode?.ToString() ?? "");
            AppendTag(sb, "aired",     m.AiredDate);
            AppendTag(sb, "plot",      m.Description);
            AppendTag(sb, "mpaa",      m.MpaRating);
            if (m.Rating > 0)
            {
                sb.AppendLine("  <ratings>");
                sb.AppendLine("    <rating name=\"imdb\" max=\"10\" default=\"true\">");
                sb.AppendLine($"      <value>{m.Rating:F1}</value>");
                sb.AppendLine($"      <votes>{m.RatingVotes}</votes>");
                sb.AppendLine("    </rating>");
                sb.AppendLine("  </ratings>");
            }
            if (!string.IsNullOrWhiteSpace(m.ImdbId))
                sb.AppendLine($"  <uniqueid type=\"imdb\">{m.ImdbId}</uniqueid>");
            if (!string.IsNullOrWhiteSpace(m.TmdbId))
                sb.AppendLine($"  <uniqueid type=\"tmdb\">{m.TmdbId}</uniqueid>");
            if (!string.IsNullOrWhiteSpace(m.TmdbSeriesId))
                sb.AppendLine($"  <uniqueid type=\"tmdb_show\">{m.TmdbSeriesId}</uniqueid>");
            if (!string.IsNullOrWhiteSpace(m.TvdbId))
                sb.AppendLine($"  <uniqueid type=\"tvdb\">{m.TvdbId}</uniqueid>");
            AppendTag(sb, "genre",    m.Genre);
            AppendTag(sb, "director", m.Director);
            foreach (var actor in (m.Cast ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                sb.AppendLine("  <actor>");
                sb.AppendLine($"    <name>{actor.Trim()}</name>");
                sb.AppendLine("  </actor>");
            }
            sb.AppendLine("</episodedetails>");
            return sb.ToString();
        }

        sb.AppendLine("<movie>");
        AppendTag(sb, "title",         m.Title);
        AppendTag(sb, "year",          m.Year);
        AppendTag(sb, "genre",         m.Genre);
        AppendTag(sb, "director",      m.Director);
        AppendTag(sb, "plot",          m.Description);
        AppendTag(sb, "mpaa",          m.MpaRating);

        if (!string.IsNullOrWhiteSpace(m.ImdbId))
        {
            AppendTag(sb, "imdbid",     m.ImdbId);
            AppendTag(sb, "uniqueid type=\"imdb\" default=\"true\"", m.ImdbId, "uniqueid");
        }
        if (!string.IsNullOrWhiteSpace(m.TmdbId))
            AppendTag(sb, "uniqueid type=\"tmdb\"", m.TmdbId, "uniqueid");

        if (!string.IsNullOrWhiteSpace(m.Cast))
        {
            foreach (var actor in m.Cast.Split(',', StringSplitOptions.TrimEntries))
            {
                if (!string.IsNullOrWhiteSpace(actor))
                {
                    sb.AppendLine("  <actor>");
                    AppendTag(sb, "name", actor, indent: 4);
                    sb.AppendLine("  </actor>");
                }
            }
        }

        sb.AppendLine("</movie>");
        return sb.ToString();
    }

    private static void AppendTag(StringBuilder sb, string tag, string? value,
        string? closingTag = null, int indent = 2)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var close = closingTag ?? tag.Split(' ')[0];
        sb.AppendLine($"{new string(' ', indent)}<{tag}>{Escape(value)}</{close}>");
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;")
         .Replace("<", "&lt;")
         .Replace(">", "&gt;")
         .Replace("\"", "&quot;");
}

internal static class StringExtensions
{
    /// <summary>Returns <paramref name="fallback"/> if the string is null or whitespace.</summary>
    public static string Or(this string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;
}
