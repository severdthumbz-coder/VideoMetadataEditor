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
}
