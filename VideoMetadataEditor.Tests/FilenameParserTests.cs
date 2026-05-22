using VideoMetadataEditor.Services;
using Xunit;

namespace VideoMetadataEditor.Tests;

public class FilenameParserTests
{
    // ── Standard scene releases ───────────────────────────────────────────────

    [Theory]
    [InlineData("The.Dark.Knight.2008.1080p.BluRay.x264",          "The Dark Knight",  "2008")]
    [InlineData("Inception.2010.720p.WEBRip.x265",                 "Inception",         "2010")]
    [InlineData("Blade.Runner.2049.2017.4K.UHD.BluRay.HDR.REMUX",  "Blade Runner 2049", "2017")]
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

    [Theory]
    [InlineData("Film.Without.Year.1080p.BluRay", "Film Without Year", "")]
    [InlineData("1917.2019.1080p",                "1917",              "2019")]  // title starts with year-like number
    [InlineData("2001.A.Space.Odyssey.1968",       "2001 A Space Odyssey", "1968")]
    public void Parse_YearEdgeCases_HandledCorrectly(string input, string expectedTitle, string expectedYear)
    {
        var (title, year) = FilenameParser.Parse(input);
        Assert.Equal(expectedTitle, title);
        Assert.Equal(expectedYear, year);
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
