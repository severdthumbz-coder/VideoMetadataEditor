using VideoMetadataEditor.Services;
using VideoMetadataEditor.Models;
using Xunit;

namespace VideoMetadataEditor.Tests;

/// <summary>
/// Build 125 — tests for NfoImportService.ParseNfo: round-trip against the build-122
/// exporter, legacy-format tolerance (flat IDs/ratings), root-type detection, and
/// malformed-input safety.
/// </summary>
public class NfoImportServiceTests
{
    // ── Round-trip: export → import preserves fields ────────────────────────────

    [Fact]
    public void RoundTrip_Movie_PreservesCoreFields()
    {
        var original = new MovieMetadata
        {
            Title = "The Dark Knight", Year = "2008",
            Description = "Batman & the Joker.", Genre = "Action, Crime, Drama",
            Director = "Christopher Nolan", Cast = "Christian Bale, Heath Ledger",
            ImdbId = "tt0468569", TmdbId = "155", Rating = 9.0f, RatingVotes = 2700000,
            MpaRating = "PG-13", IsWatched = true,
        };

        var xml = NfoExportService.BuildMovieNfo(original);
        var imported = NfoImportService.ParseNfo(xml);

        Assert.NotNull(imported);
        Assert.False(imported!.IsEpisode);
        Assert.Equal("The Dark Knight", imported.Title);
        Assert.Equal("2008", imported.Year);
        Assert.Equal("Batman & the Joker.", imported.Description); // entity round-trip
        Assert.Equal("Action, Crime, Drama", imported.Genre);
        Assert.Equal("Christopher Nolan", imported.Director);
        Assert.Equal("Christian Bale, Heath Ledger", imported.Cast);
        Assert.Equal("tt0468569", imported.ImdbId);
        Assert.Equal("155", imported.TmdbId);
        Assert.Equal(9.0f, imported.Rating);
        Assert.Equal(2700000, imported.RatingVotes);
        Assert.Equal("PG-13", imported.MpaRating);
        Assert.True(imported.IsWatched);
    }

    [Fact]
    public void RoundTrip_Episode_PreservesTvFields()
    {
        var original = new MovieMetadata
        {
            IsEpisode = true, ShowTitle = "Breaking Bad", EpisodeTitle = "Pilot",
            Season = 1, Episode = 1, Description = "Walt starts cooking.",
            AiredDate = "2008-01-20", Director = "Vince Gilligan",
            Genre = "Drama, Crime", Cast = "Bryan Cranston, Aaron Paul",
            ImdbId = "tt0959621", TvdbId = "81189", Rating = 9.5f,
        };

        var xml = NfoExportService.BuildEpisodeNfo(original);
        var imported = NfoImportService.ParseNfo(xml);

        Assert.NotNull(imported);
        Assert.True(imported!.IsEpisode);
        Assert.Equal("Pilot", imported.EpisodeTitle);
        Assert.Equal("Breaking Bad", imported.ShowTitle);
        Assert.Equal(1, imported.Season);
        Assert.Equal(1, imported.Episode);
        Assert.Equal("2008-01-20", imported.AiredDate);
        Assert.Equal("Drama, Crime", imported.Genre);
        Assert.Equal("Bryan Cranston, Aaron Paul", imported.Cast);
        Assert.Equal("tt0959621", imported.ImdbId);
        Assert.Equal("81189", imported.TvdbId);
        Assert.Equal(9.5f, imported.Rating);
    }

    [Fact]
    public void RoundTrip_MultiEpisode_ReturnsFirstBlock()
    {
        var original = new MovieMetadata
        {
            IsEpisode = true, ShowTitle = "Show", EpisodeTitle = "Two-Parter",
            Season = 1, Episode = 1,
        };
        var xml = NfoExportService.BuildEpisodeNfo(original, episodeEnd: 2);
        var imported = NfoImportService.ParseNfo(xml);

        Assert.NotNull(imported);
        Assert.Equal(1, imported!.Episode); // first block
    }

    // ── Legacy formats ───────────────────────────────────────────────────────────

    [Fact]
    public void Legacy_FlatImdbIdAndRating()
    {
        var xml = """
            <movie>
              <title>Old Movie</title>
              <imdbid>tt111</imdbid>
              <rating>7.5</rating>
              <votes>1,234</votes>
              <genre>Drama</genre>
            </movie>
            """;
        var m = NfoImportService.ParseNfo(xml);
        Assert.NotNull(m);
        Assert.Equal("tt111", m!.ImdbId);
        Assert.Equal(7.5f, m.Rating);
        Assert.Equal(1234, m.RatingVotes); // comma stripped
        Assert.Equal("Drama", m.Genre);
    }

    [Fact]
    public void Legacy_BareIdTag_ImdbWhenTtPrefixed()
    {
        var xml = "<movie><title>X</title><id>tt9999</id></movie>";
        var m = NfoImportService.ParseNfo(xml);
        Assert.Equal("tt9999", m!.ImdbId);
    }

    [Fact]
    public void Legacy_BareIdTag_TmdbWhenNumeric()
    {
        var xml = "<movie><title>X</title><id>550</id></movie>";
        var m = NfoImportService.ParseNfo(xml);
        Assert.Equal("550", m!.TmdbId);
        Assert.Equal("", m.ImdbId);
    }

    [Fact]
    public void ModernUniqueId_DefaultRatingChosen()
    {
        var xml = """
            <movie>
              <title>X</title>
              <uniqueid type="tmdb" default="true">155</uniqueid>
              <uniqueid type="imdb">tt0468569</uniqueid>
              <ratings>
                <rating name="themoviedb" max="10"><value>8.0</value></rating>
                <rating name="imdb" max="10" default="true"><value>9.0</value><votes>100</votes></rating>
              </ratings>
            </movie>
            """;
        var m = NfoImportService.ParseNfo(xml);
        Assert.Equal("155", m!.TmdbId);
        Assert.Equal("tt0468569", m.ImdbId);
        Assert.Equal(9.0f, m.Rating);   // the default="true" rating, not the first
        Assert.Equal(100, m.RatingVotes);
    }

    [Fact]
    public void MultipleGenresAndActors_Joined()
    {
        var xml = """
            <movie>
              <title>X</title>
              <genre>Action</genre><genre>Sci-Fi</genre><genre>Thriller</genre>
              <actor><name>A</name><order>0</order></actor>
              <actor><name>B</name><order>1</order></actor>
              <actor><name>A</name><order>2</order></actor>
            </movie>
            """;
        var m = NfoImportService.ParseNfo(xml);
        Assert.Equal("Action, Sci-Fi, Thriller", m!.Genre);
        Assert.Equal("A, B", m.Cast); // duplicate "A" de-duped
    }

    [Fact]
    public void TvShowRoot_MapsSeriesIdToTmdbSeriesId()
    {
        var xml = """
            <tvshow>
              <title>Breaking Bad</title>
              <plot>A chemistry teacher.</plot>
              <uniqueid type="tmdb" default="true">1396</uniqueid>
              <premiered>2008-01-20</premiered>
            </tvshow>
            """;
        var m = NfoImportService.ParseNfo(xml);
        Assert.NotNull(m);
        Assert.Equal("Breaking Bad", m!.ShowTitle);
        Assert.Equal("1396", m.TmdbSeriesId);
        Assert.Equal("2008-01-20", m.AiredDate);
    }

    [Fact]
    public void Jellyfin_PlayedMapsToWatched()
    {
        var xml = "<movie><title>X</title><played>true</played></movie>";
        var m = NfoImportService.ParseNfo(xml);
        Assert.True(m!.IsWatched);
    }

    // ── Malformed / edge ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not xml at all")]
    [InlineData("<unclosed>")]
    [InlineData("<musicvideo><title>Unknown root</title></musicvideo>")]
    public void Malformed_OrUnknownRoot_ReturnsNull(string? xml)
    {
        Assert.Null(NfoImportService.ParseNfo(xml));
    }

    [Fact]
    public void MissingOptionalFields_AreEmptyNotThrowing()
    {
        var xml = "<movie><title>Only Title</title></movie>";
        var m = NfoImportService.ParseNfo(xml);
        Assert.NotNull(m);
        Assert.Equal("Only Title", m!.Title);
        Assert.Equal("", m.Genre);
        Assert.Equal("", m.Director);
        Assert.Equal(0f, m.Rating);
        Assert.Null(m.Season);
    }

    [Fact]
    public void WhitespaceAndCaseInsensitiveTags_Handled()
    {
        // Tag casing varies in the wild; values may have surrounding whitespace.
        var xml = "<movie><Title>  Spaced  </Title><YEAR> 1999 </YEAR></movie>";
        var m = NfoImportService.ParseNfo(xml);
        Assert.Equal("Spaced", m!.Title);
        Assert.Equal("1999", m.Year);
    }
}
