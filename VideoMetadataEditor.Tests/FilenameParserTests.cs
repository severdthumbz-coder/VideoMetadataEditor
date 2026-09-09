using VideoMetadataEditor.Services;
using Xunit;

namespace VideoMetadataEditor.Tests;

public class FilenameParserTests
{
    // ── Standard scene releases ───────────────────────────────────────────────

    [Theory]
    [InlineData("The.Dark.Knight.2008.1080p.BluRay.x264",          "The Dark Knight",  "2008")]
    [InlineData("Inception.2010.720p.WEBRip.x265",                 "Inception",         "2010")]
    [InlineData("Blade.Runner.2049.2017.4K.UHD.BluRay.HDR.REMUX",  "Blade Runner", "2049")]
    [InlineData("Everything.Everywhere.All.at.Once.2022.1080p",     "Everything Everywhere All at Once", "2022")]
    [InlineData("The.Shawshank.Redemption.1994.REMASTERED.BluRay",  "The Shawshank Redemption", "1994")]
    public void Parse_SceneRelease_ExtractsTitleAndYear(string input, string expectedTitle, string expectedYear)
    {
        var (title, year) = FilenameParser.Parse(input);
        Assert.Equal(expectedTitle, title);
        Assert.Equal(expectedYear, year);
    }

    // ── Underscores ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Dune_2021_1080p",       "Dune",       "2021")]
    [InlineData("Mad_Max_Fury_Road_2015", "Mad Max Fury Road", "2015")]
    public void Parse_UnderscoreSeparators_NormalisedCorrectly(string input, string expectedTitle, string expectedYear)
    {
        var (title, year) = FilenameParser.Parse(input);
        Assert.Equal(expectedTitle, title);
        Assert.Equal(expectedYear, year);
    }

    // ── Year edge cases ───────────────────────────────────────────────────────
    // The parser uses a simple, predictable rule: the FIRST 4-digit year-like number
    // is the anchor; everything before it is the title. This means a title that *is*
    // a year-like number (1917, 2001, 2049) can't be distinguished from the release
    // year without a movie database — a known, accepted limitation. These tests pin
    // the actual deterministic behavior so regressions are caught.

    [Theory]
    [InlineData("Film.Without.Year.1080p.BluRay", "Film Without Year", "")]
    [InlineData("Mad.Max.Fury.Road.2015.1080p",   "Mad Max Fury Road", "2015")]
    public void Parse_YearEdgeCases_HandledCorrectly(string input, string expectedTitle, string expectedYear)
    {
        var (title, year) = FilenameParser.Parse(input);
        Assert.Equal(expectedTitle, title);
        Assert.Equal(expectedYear, year);
    }

    [Fact]
    public void Parse_YearLikeNumberAsTitle_TakesFirstNumberAsYear()
    {
        // "1917.2019" — the parser takes 1917 as the year (first match), leaving an
        // empty title. Documents the limitation rather than asserting impossible magic.
        var (title, year) = FilenameParser.Parse("1917.2019.1080p");
        Assert.Equal("1917", year);
        Assert.Equal("", title);
    }

    // ── Japanese/Korean scene formats ─────────────────────────────────────────

    [Theory]
    [InlineData("Spirited.Away.2001.JAPANESE.1080p.BluRay", "Spirited Away", "2001")]
    [InlineData("Parasite.2019.KOREAN.1080p.WEB-DL",        "Parasite",      "2019")]
    public void Parse_AsianSceneRelease_StripsLanguageTag(string input, string expectedTitle, string expectedYear)
    {
        var (title, year) = FilenameParser.Parse(input);
        Assert.Equal(expectedTitle, title);
        Assert.Equal(expectedYear, year);
    }

    // ── Bracket year ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("The Batman (2022)",       "The Batman", "2022")]
    [InlineData("Black Panther [2018]",    "Black Panther", "2018")]
    public void Parse_BracketYear_ExtractedCorrectly(string input, string expectedTitle, string expectedYear)
    {
        var (title, year) = FilenameParser.Parse(input);
        Assert.Equal(expectedTitle, title);
        Assert.Equal(expectedYear, year);
    }

    // ── Empty / null ──────────────────────────────────────────────────────────

    [Fact]
    public void Parse_EmptyString_ReturnsBothEmpty()
    {
        var (title, year) = FilenameParser.Parse("");
        Assert.Equal(string.Empty, title);
        Assert.Equal(string.Empty, year);
    }

    // ── Episode-only codes (E## with no season) — build 136 ─────────────────────
    // Real-world torrent naming like "Monster.E05.The.Girl.of.Heidelberg" uses a bare
    // episode number with no S## prefix. These must parse as episodes (season defaults
    // to 1) so batch rename picks the TV pattern instead of the movie pattern.

    [Theory]
    [InlineData("Monster.E05.The.Girl.of.Heidelberg.720p.DVDRip", 1, 5)]
    [InlineData("Monster.E10.A.Past.Erased.720p", 1, 10)]
    [InlineData("Monster E37 A Nameless Monster", 1, 37)]
    [InlineData("Show.E100.Finale", 1, 100)]
    public void ParseEpisode_EpisodeOnlyCode_ParsesAsSeason1(string input, int expSeason, int expEpisode)
    {
        var r = FilenameParser.ParseEpisode(input);
        Assert.NotNull(r);
        Assert.Equal(expSeason, r!.Value.season);
        Assert.Equal(expEpisode, r.Value.episode);
    }

    [Theory]
    [InlineData("Monster - S01E37 - A Nameless Monster", 1, 37)]  // full S##E## still wins
    [InlineData("Show 2x05 Title", 2, 5)]                          // NxNN still works
    public void ParseEpisode_StandardCodes_StillParse(string input, int expSeason, int expEpisode)
    {
        var r = FilenameParser.ParseEpisode(input);
        Assert.NotNull(r);
        Assert.Equal(expSeason, r!.Value.season);
        Assert.Equal(expEpisode, r.Value.episode);
    }

    // Movies must NOT be misread as episodes by the bare-E## pattern. The word-boundary
    // guards mean "E" must be a standalone token followed by digits.
    [Theory]
    [InlineData("District 9 (2009) 1080p BluRay")]
    [InlineData("Se7en (1995) 720p")]
    [InlineData("E.T. the Extra-Terrestrial (1982)")]  // "E." has a dot, not E+digits
    [InlineData("Escape.Room.2019.1080p")]              // "Escape" — E not followed by digits
    [InlineData("WALL-E (2008) 1080p")]                 // ends in E, no trailing digits
    [InlineData("Conan the Barbarian (1982)")]
    [InlineData("Ocean's Eleven (2001)")]
    public void ParseEpisode_MovieFilenames_ReturnNull(string input)
    {
        Assert.Null(FilenameParser.ParseEpisode(input));
    }
}
