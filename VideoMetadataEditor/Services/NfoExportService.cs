using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using VideoMetadataEditor.Models;

namespace VideoMetadataEditor.Services;

/// <summary>
/// Writes Kodi/XBMC-format .nfo sidecar files next to each video. The Kodi format is
/// the common standard read by Plex (NFO Agent), Jellyfin, and Emby, so one writer
/// serves all three; minor per-target switches are exposed via <see cref="NfoFlavour"/>.
///
/// Sidecar naming (Kodi/Plex/Jellyfin convention):
///   • Movie    →  &lt;video basename&gt;.nfo          (root &lt;movie&gt;)
///   • Episode  →  &lt;video basename&gt;.nfo          (root &lt;episodedetails&gt;)
///   • Series   →  tvshow.nfo  in the show folder (root &lt;tvshow&gt;)
/// A multi-episode file (when an episode-end is detected) emits multiple
/// &lt;episodedetails&gt; blocks in one .nfo, per the Plex/Kodi multi-episode convention.
///
/// The Build*Nfo methods are PURE (metadata in → XML string out, no disk/network) and
/// are unit-tested. ExportAsync is the file-writing wrapper used by the UI.
/// </summary>
public static class NfoExportService
{
    public enum NfoFlavour { Kodi, Plex, Jellyfin }

    private const string XmlDeclaration =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\" ?>";

    // ─────────────────────────────────────────────────────────────────────────
    // File-writing wrapper (UI entry point) — signature preserved for callers.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes a sidecar .nfo for each (filePath, meta) pair. When
    /// <paramref name="writeTvShowNfo"/> is true, also writes one tvshow.nfo per distinct
    /// show folder encountered among the episode files. Returns (ok, failed) counts.
    /// </summary>
    public static async Task<(int ok, int failed)> ExportAsync(
        IReadOnlyList<(string filePath, MovieMetadata meta)> files,
        IProgress<(int done, int total)>? progress = null,
        CancellationToken ct = default,
        bool writeTvShowNfo = false,
        NfoFlavour flavour = NfoFlavour.Kodi)
    {
        int ok = 0, failed = 0;
        int total = files.Count;

        // Track show folders we've already written a tvshow.nfo for, so a batch of
        // episodes from the same series only writes it once.
        var tvShowFoldersDone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (filePath, meta) = files[i];
            try
            {
                var nfoPath = SidecarPathFor(filePath);
                var xml = meta.IsEpisode
                    ? BuildEpisodeNfo(meta, episodeEnd: null, flavour)
                    : BuildMovieNfo(meta, flavour);
                await File.WriteAllTextAsync(nfoPath, xml, Utf8NoBom, ct);
                ok++;

                if (writeTvShowNfo && meta.IsEpisode)
                {
                    var showFolder = Path.GetDirectoryName(filePath) ?? string.Empty;
                    if (tvShowFoldersDone.Add(showFolder))
                    {
                        try
                        {
                            var tvPath = TvShowNfoPathFor(filePath);
                            await File.WriteAllTextAsync(tvPath, BuildTvShowNfo(meta, flavour), Utf8NoBom, ct);
                        }
                        catch { /* tvshow.nfo is best-effort; don't fail the episode export */ }
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { failed++; }

            progress?.Report((i + 1, total));
        }

        return (ok, failed);
    }

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Sidecar .nfo path: same folder and base name as the video, .nfo extension.</summary>
    public static string SidecarPathFor(string videoPath)
        => Path.ChangeExtension(videoPath, ".nfo");

    /// <summary>tvshow.nfo path in the video file's folder.</summary>
    public static string TvShowNfoPathFor(string episodeVideoPath)
        => Path.Combine(Path.GetDirectoryName(episodeVideoPath) ?? string.Empty, "tvshow.nfo");

    // ─────────────────────────────────────────────────────────────────────────
    // Pure builders (unit-tested)
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Builds a &lt;movie&gt; NFO document string.</summary>
    public static string BuildMovieNfo(MovieMetadata m, NfoFlavour flavour = NfoFlavour.Kodi)
    {
        var sb = new StringBuilder();
        sb.Append(XmlDeclaration).Append('\n');
        sb.Append("<movie>\n");
        AppendElement(sb, "title", m.Title);
        AppendElement(sb, "originaltitle", m.Title);
        AppendYear(sb, m.Year);
        AppendElement(sb, "plot", m.Description);
        AppendElement(sb, "mpaa", m.MpaRating);
        AppendGenres(sb, m.Genre);
        AppendElement(sb, "director", m.Director);
        AppendUniqueIds(sb, tmdb: m.TmdbId, imdb: m.ImdbId, tvdb: null);
        AppendRatings(sb, m);
        AppendWatched(sb, m, flavour);
        AppendActors(sb, m.Cast);
        sb.Append("</movie>\n");
        return sb.ToString();
    }

    /// <summary>
    /// Builds an &lt;episodedetails&gt; NFO document string. When <paramref name="episodeEnd"/>
    /// is supplied and greater than the start episode, emits one block per episode in the
    /// inclusive range (multi-episode file convention).
    /// </summary>
    public static string BuildEpisodeNfo(MovieMetadata m, int? episodeEnd = null,
        NfoFlavour flavour = NfoFlavour.Kodi)
    {
        var sb = new StringBuilder();
        sb.Append(XmlDeclaration).Append('\n');

        int start = m.Episode ?? 0;
        int end   = (episodeEnd.HasValue && m.Episode.HasValue && episodeEnd.Value > m.Episode.Value)
            ? episodeEnd.Value : start;

        for (int ep = start; ep <= end; ep++)
        {
            sb.Append("<episodedetails>\n");
            AppendElement(sb, "title", FirstNonEmpty(m.EpisodeTitle, m.Title));
            AppendElement(sb, "showtitle", m.ShowTitle);
            if (m.Season.HasValue)
                AppendElement(sb, "season", m.Season.Value.ToString(CultureInfo.InvariantCulture));
            AppendElement(sb, "episode", ep.ToString(CultureInfo.InvariantCulture));
            AppendElement(sb, "plot", m.Description);
            AppendElement(sb, "aired", m.AiredDate);
            AppendElement(sb, "mpaa", m.MpaRating);
            AppendGenres(sb, m.Genre);
            AppendElement(sb, "director", m.Director);
            AppendUniqueIds(sb, tmdb: m.TmdbId, imdb: m.ImdbId, tvdb: m.TvdbId);
            if (!string.IsNullOrWhiteSpace(m.TmdbSeriesId))
                AppendRawUniqueId(sb, "tmdb_show", m.TmdbSeriesId);
            AppendRatings(sb, m);
            AppendWatched(sb, m, flavour);
            AppendActors(sb, m.Cast);
            sb.Append("</episodedetails>\n");
        }
        return sb.ToString();
    }

    /// <summary>Builds a series-level &lt;tvshow&gt; NFO document string.</summary>
    public static string BuildTvShowNfo(MovieMetadata m, NfoFlavour flavour = NfoFlavour.Kodi)
    {
        var sb = new StringBuilder();
        sb.Append(XmlDeclaration).Append('\n');
        sb.Append("<tvshow>\n");
        AppendElement(sb, "title", FirstNonEmpty(m.ShowTitle, m.Title));
        AppendElement(sb, "plot", m.Description);
        AppendElement(sb, "mpaa", m.MpaRating);
        AppendGenres(sb, m.Genre);
        AppendPremiered(sb, m.AiredDate);
        AppendUniqueIds(sb, tmdb: FirstNonEmpty(m.TmdbSeriesId, m.TmdbId), imdb: m.ImdbId, tvdb: m.TvdbId);
        AppendRatings(sb, m);
        AppendActors(sb, m.Cast);
        sb.Append("</tvshow>\n");
        return sb.ToString();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Element helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static void AppendElement(StringBuilder sb, string tag, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        sb.Append("  <").Append(tag).Append('>')
          .Append(Escape(value))
          .Append("</").Append(tag).Append(">\n");
    }

    private static void AppendYear(StringBuilder sb, string? year)
    {
        if (!string.IsNullOrWhiteSpace(year) && int.TryParse(year, out _))
            AppendElement(sb, "year", year);
    }

    private static void AppendGenres(StringBuilder sb, string? genreCsv)
    {
        if (string.IsNullOrWhiteSpace(genreCsv)) return;
        foreach (var g in SplitCsv(genreCsv))
            AppendElement(sb, "genre", g);
    }

    /// <summary>Emits &lt;premiered&gt; only when the value parses as a full date.</summary>
    private static void AppendPremiered(StringBuilder sb, string? airedDate)
    {
        if (!string.IsNullOrWhiteSpace(airedDate)
            && DateTime.TryParse(airedDate, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
            AppendElement(sb, "premiered", dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Emits &lt;uniqueid&gt; elements; the first non-empty id (tmdb &gt; imdb &gt; tvdb) is
    /// marked default="true" per the modern Kodi/Plex convention.
    /// </summary>
    private static void AppendUniqueIds(StringBuilder sb, string? tmdb, string? imdb, string? tvdb)
    {
        bool defaultUsed = false;
        AppendUniqueId(sb, "tmdb", tmdb, ref defaultUsed);
        AppendUniqueId(sb, "imdb", imdb, ref defaultUsed);
        AppendUniqueId(sb, "tvdb", tvdb, ref defaultUsed);
    }

    private static void AppendUniqueId(StringBuilder sb, string type, string? value, ref bool defaultUsed)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var def = defaultUsed ? "" : " default=\"true\"";
        defaultUsed = true;
        sb.Append("  <uniqueid type=\"").Append(type).Append('"').Append(def).Append('>')
          .Append(Escape(value)).Append("</uniqueid>\n");
    }

    /// <summary>A non-default uniqueid (e.g. tmdb_show) without the default flag.</summary>
    private static void AppendRawUniqueId(StringBuilder sb, string type, string value)
        => sb.Append("  <uniqueid type=\"").Append(type).Append("\">")
             .Append(Escape(value)).Append("</uniqueid>\n");

    private static void AppendRatings(StringBuilder sb, MovieMetadata m)
    {
        if (m.Rating <= 0f) return;
        var rating = m.Rating.ToString("0.0", CultureInfo.InvariantCulture);
        sb.Append("  <ratings>\n");
        sb.Append("    <rating name=\"imdb\" max=\"10\" default=\"true\">\n");
        sb.Append("      <value>").Append(rating).Append("</value>\n");
        if (m.RatingVotes > 0)
            sb.Append("      <votes>")
              .Append(m.RatingVotes.ToString(CultureInfo.InvariantCulture))
              .Append("</votes>\n");
        sb.Append("    </rating>\n");
        sb.Append("  </ratings>\n");
    }

    /// <summary>Kodi/Plex read &lt;watched&gt;; Jellyfin also recognises &lt;played&gt;.</summary>
    private static void AppendWatched(StringBuilder sb, MovieMetadata m, NfoFlavour flavour)
    {
        if (!m.IsWatched) return;
        AppendElement(sb, "watched", "true");
        if (flavour == NfoFlavour.Jellyfin)
            AppendElement(sb, "played", "true");
    }

    private static void AppendActors(StringBuilder sb, string? castCsv)
    {
        if (string.IsNullOrWhiteSpace(castCsv)) return;
        int order = 0;
        foreach (var name in SplitCsv(castCsv))
        {
            sb.Append("  <actor>\n");
            sb.Append("    <name>").Append(Escape(name)).Append("</name>\n");
            sb.Append("    <order>").Append(order.ToString(CultureInfo.InvariantCulture)).Append("</order>\n");
            sb.Append("  </actor>\n");
            order++;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Utilities
    // ─────────────────────────────────────────────────────────────────────────

    private static IEnumerable<string> SplitCsv(string csv)
        => csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? string.Empty;

    /// <summary>XML-escapes the five predefined entities. Ampersand must be replaced first.</summary>
    private static string Escape(string s)
        => s.Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;")
            .Replace("\"", "&quot;")
            .Replace("'", "&apos;");
}

internal static class StringExtensions
{
    /// <summary>Returns <paramref name="fallback"/> if the string is null or whitespace.</summary>
    public static string Or(this string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;
}
