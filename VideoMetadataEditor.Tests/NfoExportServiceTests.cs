using System.Xml.Linq;
using VideoMetadataEditor.Services;
using VideoMetadataEditor.Models;
using Xunit;

namespace VideoMetadataEditor.Tests;

/// <summary>
/// Build 122 — tests for NfoExportService pure XML builders (BuildMovieNfo,
/// BuildEpisodeNfo, BuildTvShowNfo). Asserts well-formed XML, correct field mapping,
/// escaping, uniqueid default flagging, multi-episode blocks, and the genre/actor split.
/// The file-writing wrapper (ExportAsync) is not covered here.
/// </summary>
public class NfoExportServiceTests
{
    private static MovieMetadata Movie() => new()
    {
        Title = "The Dark Knight", Year = "2008",
        Description = "Batman raises the stakes.", Genre = "Action, Crime, Drama",
        Director = "Christopher Nolan", Cast = "Christian Bale, Heath Ledger",
        ImdbId = "tt0468569", TmdbId = "155", Rating = 9.0f, RatingVotes = 2700000,
        MpaRating = "PG-13",
    };

    private static MovieMetadata Episode() => new()
    {
        IsEpisode = true, ShowTitle = "Breaking Bad", EpisodeTitle = "Pilot",
        Season = 1, Episode = 1, Description = "Walt starts cooking.",
        AiredDate = "2008-01-20", Director = "Vince Gilligan",
        Genre = "Drama, Crime", Cast = "Bryan Cranston, Aaron Paul",
        ImdbId = "tt0959621", TmdbId = "62085", TvdbId = "81189",
        Rating = 9.5f, RatingVotes = 50000, MpaRating = "TV-14",
    };

    // ── Well-formedness ─────────────────────────────────────────────────────────

    [Fact]
    public void MovieNfo_IsWellFormedXml_WithMovieRoot()
    {
        var xml = NfoExportService.BuildMovieNfo(Movie());
        var doc = XDocument.Parse(xml);
        Assert.Equal("movie", doc.Root!.Name.LocalName);
    }

    [Fact]
    public void EpisodeNfo_IsWellFormedXml_WithEpisodeDetailsRoot()
    {
        var xml = NfoExportService.BuildEpisodeNfo(Episode());
        var doc = XDocument.Parse(xml);
        Assert.Equal("episodedetails", doc.Root!.Name.LocalName);
    }

    [Fact]
    public void TvShowNfo_IsWellFormedXml_WithTvShowRoot()
    {
        var xml = NfoExportService.BuildTvShowNfo(Episode());
        var doc = XDocument.Parse(xml);
        Assert.Equal("tvshow", doc.Root!.Name.LocalName);
    }

    // ── Field mapping ────────────────────────────────────────────────────────────

    [Fact]
    public void MovieNfo_MapsCoreFields()
    {
        var doc = XDocument.Parse(NfoExportService.BuildMovieNfo(Movie()));
        var r = doc.Root!;
        Assert.Equal("The Dark Knight", r.Element("title")!.Value);
        Assert.Equal("The Dark Knight", r.Element("originaltitle")!.Value);
        Assert.Equal("2008", r.Element("year")!.Value);
        Assert.Equal("Batman raises the stakes.", r.Element("plot")!.Value);
        Assert.Equal("PG-13", r.Element("mpaa")!.Value);
        Assert.Equal("Christopher Nolan", r.Element("director")!.Value);
    }

    [Fact]
    public void MovieNfo_SplitsGenresIntoSeparateElements()
    {
        var doc = XDocument.Parse(NfoExportService.BuildMovieNfo(Movie()));
        var genres = doc.Root!.Elements("genre").Select(e => e.Value).ToList();
        Assert.Equal(new[] { "Action", "Crime", "Drama" }, genres);
    }

    [Fact]
    public void MovieNfo_ActorsHaveOrderInSequence()
    {
        var doc = XDocument.Parse(NfoExportService.BuildMovieNfo(Movie()));
        var actors = doc.Root!.Elements("actor").ToList();
        Assert.Equal(2, actors.Count);
        Assert.Equal("Christian Bale", actors[0].Element("name")!.Value);
        Assert.Equal("0", actors[0].Element("order")!.Value);
        Assert.Equal("Heath Ledger", actors[1].Element("name")!.Value);
        Assert.Equal("1", actors[1].Element("order")!.Value);
    }

    [Fact]
    public void MovieNfo_FirstUniqueIdIsDefault()
    {
        var doc = XDocument.Parse(NfoExportService.BuildMovieNfo(Movie()));
        var ids = doc.Root!.Elements("uniqueid").ToList();
        // tmdb is emitted first → it is the default
        var tmdb = ids.First(e => (string?)e.Attribute("type") == "tmdb");
        var imdb = ids.First(e => (string?)e.Attribute("type") == "imdb");
        Assert.Equal("true", (string?)tmdb.Attribute("default"));
        Assert.Null(imdb.Attribute("default"));
        Assert.Equal("155", tmdb.Value);
        Assert.Equal("tt0468569", imdb.Value);
    }

    [Fact]
    public void MovieNfo_RatingsBlock_HasValueAndVotes()
    {
        var doc = XDocument.Parse(NfoExportService.BuildMovieNfo(Movie()));
        var rating = doc.Root!.Element("ratings")!.Element("rating")!;
        Assert.Equal("9.0", rating.Element("value")!.Value);
        Assert.Equal("2700000", rating.Element("votes")!.Value);
        Assert.Equal("true", (string?)rating.Attribute("default"));
    }

    [Fact]
    public void EpisodeNfo_MapsSeasonEpisodeAndShowTitle()
    {
        var doc = XDocument.Parse(NfoExportService.BuildEpisodeNfo(Episode()));
        var r = doc.Root!;
        Assert.Equal("Pilot", r.Element("title")!.Value);
        Assert.Equal("Breaking Bad", r.Element("showtitle")!.Value);
        Assert.Equal("1", r.Element("season")!.Value);
        Assert.Equal("1", r.Element("episode")!.Value);
        Assert.Equal("2008-01-20", r.Element("aired")!.Value);
    }

    // ── Multi-episode ─────────────────────────────────────────────────────────────

    [Fact]
    public void EpisodeNfo_MultiEpisode_EmitsOneBlockPerEpisode()
    {
        var xml = NfoExportService.BuildEpisodeNfo(Episode(), episodeEnd: 3);
        // Wrap in a synthetic root so multiple top-level elements parse.
        var doc = XDocument.Parse("<root>" + StripDeclaration(xml) + "</root>");
        var blocks = doc.Root!.Elements("episodedetails").ToList();
        Assert.Equal(3, blocks.Count);
        Assert.Equal("1", blocks[0].Element("episode")!.Value);
        Assert.Equal("2", blocks[1].Element("episode")!.Value);
        Assert.Equal("3", blocks[2].Element("episode")!.Value);
    }

    [Fact]
    public void EpisodeNfo_EndNotGreater_SingleBlock()
    {
        var xml = NfoExportService.BuildEpisodeNfo(Episode(), episodeEnd: 1);
        var doc = XDocument.Parse(xml);
        Assert.Equal("episodedetails", doc.Root!.Name.LocalName);
        Assert.Equal("1", doc.Root!.Element("episode")!.Value);
    }

    // ── Escaping ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Escaping_AmpersandsAndAnglesAndQuotes_ProduceValidXml()
    {
        var m = new MovieMetadata
        {
            Title = "Tom & Jerry <Special> \"Quote\"",
            Description = "A & B < C > D",
            Cast = "O'Brien, A & B",
        };
        var xml = NfoExportService.BuildMovieNfo(m);
        var doc = XDocument.Parse(xml); // throws if escaping is wrong
        Assert.Equal("Tom & Jerry <Special> \"Quote\"", doc.Root!.Element("title")!.Value);
        var actors = doc.Root!.Elements("actor").Select(a => a.Element("name")!.Value).ToList();
        Assert.Contains("O'Brien", actors);
        Assert.Contains("A & B", actors);
    }

    // ── Empty-field omission ───────────────────────────────────────────────────────

    [Fact]
    public void EmptyFields_AreOmitted_NotEmptyElements()
    {
        var m = new MovieMetadata { Title = "Only Title" };
        var doc = XDocument.Parse(NfoExportService.BuildMovieNfo(m));
        Assert.Equal("Only Title", doc.Root!.Element("title")!.Value);
        Assert.Null(doc.Root!.Element("plot"));      // empty → omitted
        Assert.Null(doc.Root!.Element("director"));  // empty → omitted
        Assert.Empty(doc.Root!.Elements("genre"));   // empty → no genre elements
        Assert.Null(doc.Root!.Element("ratings"));   // rating 0 → omitted
    }

    [Fact]
    public void NoRating_OmitsRatingsBlock()
    {
        var m = Movie();
        m.Rating = 0f;
        var doc = XDocument.Parse(NfoExportService.BuildMovieNfo(m));
        Assert.Null(doc.Root!.Element("ratings"));
    }

    // ── Watched / flavour ──────────────────────────────────────────────────────────

    [Fact]
    public void Watched_Kodi_EmitsWatchedOnly()
    {
        var m = Movie();
        m.IsWatched = true;
        var doc = XDocument.Parse(NfoExportService.BuildMovieNfo(m, NfoExportService.NfoFlavour.Kodi));
        Assert.Equal("true", doc.Root!.Element("watched")!.Value);
        Assert.Null(doc.Root!.Element("played"));
    }

    [Fact]
    public void Watched_Jellyfin_AlsoEmitsPlayed()
    {
        var m = Movie();
        m.IsWatched = true;
        var doc = XDocument.Parse(NfoExportService.BuildMovieNfo(m, NfoExportService.NfoFlavour.Jellyfin));
        Assert.Equal("true", doc.Root!.Element("watched")!.Value);
        Assert.Equal("true", doc.Root!.Element("played")!.Value);
    }

    // ── tvshow.nfo ──────────────────────────────────────────────────────────────────

    [Fact]
    public void TvShowNfo_UsesShowTitleAndSeriesId()
    {
        var e = Episode();
        e.TmdbSeriesId = "1396";
        var doc = XDocument.Parse(NfoExportService.BuildTvShowNfo(e));
        Assert.Equal("Breaking Bad", doc.Root!.Element("title")!.Value);
        var tmdb = doc.Root!.Elements("uniqueid").First(x => (string?)x.Attribute("type") == "tmdb");
        Assert.Equal("1396", tmdb.Value);  // series id preferred over episode TmdbId
        Assert.Equal("2008-01-20", doc.Root!.Element("premiered")!.Value);
    }

    // ── Path helpers ──────────────────────────────────────────────────────────────

    [Fact]
    public void SidecarPath_ReplacesExtensionWithNfo()
    {
        var p = NfoExportService.SidecarPathFor(System.IO.Path.Combine("X", "Movie (2024).mp4"));
        Assert.EndsWith("Movie (2024).nfo", p);
    }

    private static string StripDeclaration(string xml)
    {
        int idx = xml.IndexOf("?>", System.StringComparison.Ordinal);
        return idx >= 0 ? xml[(idx + 2)..] : xml;
    }
}
