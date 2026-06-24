using VideoMetadataEditor.Services;
using VideoMetadataEditor.Models;
using Xunit;

namespace VideoMetadataEditor.Tests;

/// <summary>
/// Build 120 — tests for FilenameParser.ParseEpisodeRange (multi-episode range
/// detection from source filenames) and FileRenameService grammar extensions
/// (conditional [ ] blocks, {Token:00} zero-pad, range expansion).
/// All pure logic — fully CI-testable under NO_WPF.
/// </summary>
public class EpisodeRangeAndGrammarTests
{
    private static readonly FileRenameService Svc = new();

    // ── ParseEpisodeRange ──────────────────────────────────────────────────────

    [Theory]
    [InlineData("Show.S01E01-E03.1080p", 3)]
    [InlineData("Show.S01E01-03", 3)]
    [InlineData("Show.s01e05-e08.x265", 8)]
    [InlineData("Show 1x01-1x03", 3)]
    [InlineData("Show 1x01-03", 3)]
    [InlineData("Show.S01E01E02E03", 3)]
    [InlineData("Show.S02E10E11", 11)]
    public void ParseEpisodeRange_DetectsEnd(string name, int expectedEnd)
    {
        Assert.Equal(expectedEnd, FilenameParser.ParseEpisodeRange(name));
    }

    [Theory]
    [InlineData("Show.S01E05.1080p")]          // single episode, no range
    [InlineData("Movie.2019.1080p.BluRay")]    // not an episode at all
    [InlineData("Show.S01E03-E01")]            // end < start → ignored
    [InlineData("Show.S01E02-E02")]            // end == start → ignored
    public void ParseEpisodeRange_ReturnsNull_WhenNoValidRange(string name)
    {
        Assert.Null(FilenameParser.ParseEpisodeRange(name));
    }

    [Fact]
    public void ParseEpisodeRange_NullInput_ReturnsNull()
    {
        Assert.Null(FilenameParser.ParseEpisodeRange(null!));
    }

    // ── Range expansion in BuildFileName ───────────────────────────────────────

    private static MovieMetadata Episode(int season, int episode, string show = "Breaking Bad",
        string epTitle = "")
        => new() { IsEpisode = true, ShowTitle = show, Season = season, Episode = episode,
                   EpisodeTitle = epTitle };

    [Fact]
    public void EpisodeRange_ExpandsWhenEndProvided()
    {
        var result = Svc.BuildFileName("{ShowTitle} S{Season}E{Episode}",
            Episode(1, 1), ".mkv", episodeEnd: 3);
        Assert.Equal("Breaking Bad S01E01-03.mkv", result);
    }

    [Fact]
    public void EpisodeRange_NoExpansionWhenEndNull()
    {
        var result = Svc.BuildFileName("{ShowTitle} S{Season}E{Episode}",
            Episode(1, 1), ".mkv", episodeEnd: null);
        Assert.Equal("Breaking Bad S01E01.mkv", result);
    }

    [Fact]
    public void EpisodeRange_NoExpansionWhenEndNotGreater()
    {
        var result = Svc.BuildFileName("{ShowTitle} S{Season}E{Episode}",
            Episode(1, 5), ".mkv", episodeEnd: 5);
        Assert.Equal("Breaking Bad S01E05.mkv", result);
    }

    // ── Zero-pad format spec ────────────────────────────────────────────────────

    [Fact]
    public void PadSpec_ThreeDigitEpisode()
    {
        var result = Svc.BuildFileName("{ShowTitle} S{Season:00}E{Episode:000}",
            Episode(1, 5), ".mkv");
        Assert.Equal("Breaking Bad S01E005.mkv", result);
    }

    [Fact]
    public void PadSpec_DefaultsToWidthTwo_WhenNoSpec()
    {
        var result = Svc.BuildFileName("S{Season}E{Episode}", Episode(2, 7), ".mkv");
        Assert.Equal("S02E07.mkv", result);
    }

    [Fact]
    public void PadSpec_RangeRespectsPadWidth()
    {
        var result = Svc.BuildFileName("E{Episode:000}", Episode(1, 1), ".mkv", episodeEnd: 12);
        Assert.Equal("E001-012.mkv", result);
    }

    // ── Conditional [ ] blocks ──────────────────────────────────────────────────

    [Fact]
    public void Conditional_DroppedWhenTokenEmpty()
    {
        var result = Svc.BuildFileName("{ShowTitle} S{Season}E{Episode}[ - {EpisodeTitle}]",
            Episode(1, 1, epTitle: ""), ".mkv");
        Assert.Equal("Breaking Bad S01E01.mkv", result);
    }

    [Fact]
    public void Conditional_KeptWhenTokenPresent()
    {
        var result = Svc.BuildFileName("{ShowTitle} S{Season}E{Episode}[ - {EpisodeTitle}]",
            Episode(1, 1, epTitle: "Pilot"), ".mkv");
        Assert.Equal("Breaking Bad S01E01 - Pilot.mkv", result);
    }

    [Fact]
    public void Conditional_MultipleBlocks_Independent()
    {
        var meta = new MovieMetadata
        {
            IsEpisode = true, ShowTitle = "Show", Season = 1, Episode = 2,
            EpisodeTitle = "", AiredDate = "2020-01-01"
        };
        var result = Svc.BuildFileName("{ShowTitle}[ - {EpisodeTitle}][ ({Aired})]", meta, ".mkv");
        Assert.Equal("Show (2020-01-01).mkv", result);
    }

    [Fact]
    public void Conditional_BlockWithMultipleTokens_DroppedIfAnyEmpty()
    {
        var meta = new MovieMetadata
        {
            IsEpisode = true, ShowTitle = "Show", Season = 1, Episode = 2,
            Director = "", Year = "2020"
        };
        // Block needs BOTH Director and Year; Director empty → whole block dropped
        var result = Svc.BuildFileName("{ShowTitle}[ - {Director} {Year}]", meta, ".mkv");
        Assert.Equal("Show.mkv", result);
    }

    // ── Regression: legacy patterns unchanged ───────────────────────────────────

    [Fact]
    public void Legacy_MoviePattern_Unchanged()
    {
        var meta = new MovieMetadata { Title = "Test Movie", Year = "2024" };
        var result = Svc.BuildFileName("{Title} ({Year})", meta, ".mp4");
        Assert.Equal("Test Movie (2024).mp4", result);
    }

    [Fact]
    public void Legacy_TvDefaultPattern_Unchanged()
    {
        var meta = Episode(2, 5, show: "Breaking Bad", epTitle: "Breakage");
        var result = Svc.BuildFileName("{ShowTitle} - S{Season}E{Episode} - {EpisodeTitle}",
            meta, ".mkv");
        Assert.Equal("Breaking Bad - S02E05 - Breakage.mkv", result);
    }

    [Fact]
    public void Legacy_EmptyEpisodeTitle_TrailingSeparatorTrimmed()
    {
        // Legacy flat-replace produced "Breaking Bad - S02E05 - " then
        // Trim(' ','.','-') stripped the trailing " - ". Tokenizer must match.
        var meta = Episode(2, 5, show: "Breaking Bad", epTitle: "");
        var result = Svc.BuildFileName("{ShowTitle} - S{Season}E{Episode} - {EpisodeTitle}",
            meta, ".mkv");
        Assert.Equal("Breaking Bad - S02E05.mkv", result);
    }

    [Fact]
    public void UnknownToken_LeftIntact()
    {
        var meta = new MovieMetadata { Title = "X" };
        var result = Svc.BuildFileName("{Title} {Bogus}", meta, ".mp4");
        Assert.Equal("X {Bogus}.mp4", result);
    }
}
