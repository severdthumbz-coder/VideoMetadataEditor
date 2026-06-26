using System.Globalization;
using System.Xml.Linq;
using VideoMetadataEditor.Models;

namespace VideoMetadataEditor.Services;

/// <summary>
/// Parses Kodi/XBMC-format .nfo files back into <see cref="MovieMetadata"/>. The inverse
/// of <see cref="NfoExportService"/>. Built to tolerate real-world variation, since NFOs
/// on disk are written by many tools (Kodi, TinyMediaManager, MediaElch, Sonarr/Radarr…):
///   • IDs: modern &lt;uniqueid type="imdb"&gt; AND legacy flat &lt;imdbid&gt;/&lt;tmdbid&gt;/&lt;id&gt;.
///   • Ratings: modern &lt;ratings&gt;&lt;rating&gt;&lt;value&gt; AND legacy flat &lt;rating&gt;9.0&lt;/rating&gt;.
///   • Multiple &lt;genre&gt; / &lt;actor&gt; elements collapse to VME's comma lists.
///   • Missing/extra/unknown tags are ignored; entity-escaped text is decoded by the parser.
///
/// <see cref="ParseNfo"/> is pure (XML string in → metadata out, no disk/network) and is
/// fully unit-tested. File reading lives in the caller.
/// </summary>
public static class NfoImportService
{
    /// <summary>
    /// Parses NFO XML into a <see cref="MovieMetadata"/>. Recognises &lt;movie&gt;,
    /// &lt;episodedetails&gt;, and &lt;tvshow&gt; roots. Returns null if the XML is malformed
    /// or the root is not a recognised NFO type. When an NFO contains multiple
    /// &lt;episodedetails&gt; blocks (multi-episode file), the FIRST block is returned.
    /// </summary>
    public static MovieMetadata? ParseNfo(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;

        XElement root;
        try
        {
            // Some NFOs carry trailing junk or multiple roots; wrap defensively only if needed.
            var doc = XDocument.Parse(xml);
            root = doc.Root!;
        }
        catch
        {
            return null;
        }
        if (root is null) return null;

        return root.Name.LocalName.ToLowerInvariant() switch
        {
            "movie"          => ParseMovie(root),
            "episodedetails" => ParseEpisode(root),
            "tvshow"         => ParseTvShow(root),
            _                => null,
        };
    }

    // ─────────────────────────────────────────────────────────────────────────

    private static MovieMetadata ParseMovie(XElement root)
    {
        var m = new MovieMetadata { IsEpisode = false, Source = "NFO" };
        m.Title       = Str(root, "title", "originaltitle");
        m.Year        = Str(root, "year");
        m.Description = Str(root, "plot", "outline");
        m.MpaRating   = Str(root, "mpaa");
        m.Director    = JoinElements(root, "director");
        m.Genre       = JoinElements(root, "genre");
        m.Cast        = JoinActors(root);
        ApplyIds(root, m, isSeries: false);
        ApplyRating(root, m);
        m.IsWatched   = ParseWatched(root);
        return m;
    }

    private static MovieMetadata ParseEpisode(XElement root)
    {
        var m = new MovieMetadata { IsEpisode = true, Source = "NFO" };
        m.EpisodeTitle = Str(root, "title");
        m.ShowTitle    = Str(root, "showtitle");
        m.Season       = Int(root, "season");
        m.Episode      = Int(root, "episode");
        m.Description  = Str(root, "plot", "outline");
        m.AiredDate    = Str(root, "aired", "premiered");
        m.MpaRating    = Str(root, "mpaa");
        m.Director     = JoinElements(root, "director");
        m.Genre        = JoinElements(root, "genre");
        m.Cast         = JoinActors(root);
        ApplyIds(root, m, isSeries: false);
        ApplyRating(root, m);
        m.IsWatched    = ParseWatched(root);
        return m;
    }

    private static MovieMetadata ParseTvShow(XElement root)
    {
        var m = new MovieMetadata { IsEpisode = true, Source = "NFO" };
        m.ShowTitle   = Str(root, "title");
        m.Description = Str(root, "plot", "outline");
        m.MpaRating   = Str(root, "mpaa");
        m.Genre       = JoinElements(root, "genre");
        m.Cast        = JoinActors(root);
        m.AiredDate   = Str(root, "premiered", "aired");
        ApplyIds(root, m, isSeries: true);
        ApplyRating(root, m);
        return m;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Field helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>First non-empty value among the named child elements (case-insensitive tag match).</summary>
    private static string Str(XElement root, params string[] tagNames)
    {
        foreach (var tag in tagNames)
        {
            var el = Child(root, tag);
            if (el != null && !string.IsNullOrWhiteSpace(el.Value))
                return el.Value.Trim();
        }
        return string.Empty;
    }

    private static int? Int(XElement root, string tag)
    {
        var el = Child(root, tag);
        if (el != null && int.TryParse(el.Value.Trim(),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            return n;
        return null;
    }

    /// <summary>Joins all matching child elements' values into a ", " list (e.g. multiple &lt;genre&gt;).</summary>
    private static string JoinElements(XElement root, string tag)
        => string.Join(", ", Children(root, tag)
            .Select(e => e.Value.Trim())
            .Where(v => !string.IsNullOrWhiteSpace(v)));

    /// <summary>Joins all &lt;actor&gt;&lt;name&gt; values into a ", " list, preserving order, de-duplicated.</summary>
    private static string JoinActors(XElement root)
    {
        var names = new List<string>();
        var seen  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var actor in Children(root, "actor"))
        {
            var name = Child(actor, "name")?.Value.Trim();
            if (!string.IsNullOrWhiteSpace(name) && seen.Add(name))
                names.Add(name);
        }
        return string.Join(", ", names);
    }

    /// <summary>
    /// Applies IDs from both modern &lt;uniqueid type="..."&gt; and legacy flat forms
    /// (&lt;imdbid&gt;, &lt;tmdbid&gt;, &lt;tvdbid&gt;, &lt;id&gt;). For a series NFO, a tmdb id is
    /// stored as TmdbSeriesId; otherwise as TmdbId.
    /// </summary>
    private static void ApplyIds(XElement root, MovieMetadata m, bool isSeries)
    {
        string imdb = "", tmdb = "", tvdb = "";

        foreach (var uid in Children(root, "uniqueid"))
        {
            var type = (string?)uid.Attribute("type") ?? "";
            var val  = uid.Value.Trim();
            if (string.IsNullOrWhiteSpace(val)) continue;
            switch (type.ToLowerInvariant())
            {
                case "imdb": imdb = imdb.Length == 0 ? val : imdb; break;
                case "tmdb": tmdb = tmdb.Length == 0 ? val : tmdb; break;
                case "tvdb": tvdb = tvdb.Length == 0 ? val : tvdb; break;
            }
        }

        // Legacy flat tags fill anything still empty.
        if (imdb.Length == 0) imdb = Str(root, "imdbid");
        if (tmdb.Length == 0) tmdb = Str(root, "tmdbid");
        if (tvdb.Length == 0) tvdb = Str(root, "tvdbid");
        // Bare <id> is most often an IMDb id when it starts with "tt", else TMDB numeric.
        if (imdb.Length == 0 && tmdb.Length == 0)
        {
            var bareId = Str(root, "id");
            if (bareId.StartsWith("tt", StringComparison.OrdinalIgnoreCase)) imdb = bareId;
            else if (bareId.Length > 0) tmdb = bareId;
        }

        if (imdb.Length > 0) m.ImdbId = imdb;
        if (tvdb.Length > 0) m.TvdbId = tvdb;
        if (tmdb.Length > 0)
        {
            if (isSeries) m.TmdbSeriesId = tmdb;
            else          m.TmdbId       = tmdb;
        }
    }

    /// <summary>
    /// Applies rating + votes from the modern &lt;ratings&gt;&lt;rating&gt;&lt;value&gt; block,
    /// falling back to a legacy flat &lt;rating&gt;9.0&lt;/rating&gt;. Votes from &lt;votes&gt;.
    /// </summary>
    private static void ApplyRating(XElement root, MovieMetadata m)
    {
        // Modern: <ratings><rating ...><value>..</value><votes>..</votes></rating></ratings>
        var ratingsEl = Child(root, "ratings");
        if (ratingsEl != null)
        {
            // Prefer the rating flagged default="true", else the first.
            var ratingEls = Children(ratingsEl, "rating").ToList();
            var chosen = ratingEls.FirstOrDefault(r =>
                            string.Equals((string?)r.Attribute("default"), "true",
                                StringComparison.OrdinalIgnoreCase))
                         ?? ratingEls.FirstOrDefault();
            if (chosen != null)
            {
                var valEl = Child(chosen, "value");
                if (valEl != null && TryFloat(valEl.Value, out float v)) m.Rating = v;
                var votesEl = Child(chosen, "votes");
                if (votesEl != null && int.TryParse(
                        new string(votesEl.Value.Where(char.IsDigit).ToArray()),
                        out int votes)) m.RatingVotes = votes;
                return;
            }
        }

        // Legacy: flat <rating>9.0</rating>
        var flat = Child(root, "rating");
        if (flat != null && TryFloat(flat.Value, out float fv)) m.Rating = fv;
        var flatVotes = Child(root, "votes");
        if (flatVotes != null && int.TryParse(
                new string(flatVotes.Value.Where(char.IsDigit).ToArray()), out int fvotes))
            m.RatingVotes = fvotes;
    }

    private static bool ParseWatched(XElement root)
    {
        // <watched>true</watched> (Kodi/Plex) or <played>true</played> (Jellyfin).
        var w = Str(root, "watched", "played");
        return string.Equals(w, "true", StringComparison.OrdinalIgnoreCase);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // XML utilities (case-insensitive element lookup)
    // ─────────────────────────────────────────────────────────────────────────

    private static XElement? Child(XElement parent, string localName)
        => parent.Elements().FirstOrDefault(e =>
            string.Equals(e.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<XElement> Children(XElement parent, string localName)
        => parent.Elements().Where(e =>
            string.Equals(e.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase));

    private static bool TryFloat(string s, out float value)
        => float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
}
