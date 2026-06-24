using VideoMetadataEditor.Services;
using VideoMetadataEditor.Models;
using Xunit;

namespace VideoMetadataEditor.Tests;

/// <summary>
/// Build 121 — tests for FilenameParser.ParseAbsoluteEpisode (anime absolute-number
/// detection from source filenames) and the {AbsoluteEpisode} rename token.
/// The AniList relations-graph fallback (AniListApiService.ComputeAbsoluteEpisodeAsync)
/// is a live-network path and is intentionally not covered here.
/// </summary>
public class AbsoluteEpisodeTests
{
    private static readonly FileRenameService Svc = new();

    // ── ParseAbsoluteEpisode ────────────────────────────────────────────────────

    [Theory]
    [InlineData("[SubsGroup] Show Name - 153 [1080p]", 153)]
    [InlineData("Show Name - 153 (1080p)", 153)]
    [InlineData("Show Name - 153", 153)]
    [InlineData("[Group] Show Name 153 [BD]", 153)]
    [InlineData("Show Name E153", 153)]
    [InlineData("Show Name Ep153", 153)]
    [InlineData("Show Name Episode 153", 153)]
    [InlineData("Show Name - 12", 12)]
    [InlineData("One Piece - 1086 [1080p]", 1086)]
    public void ParseAbsoluteEpisode_DetectsNumber(string name, int expected)
    {
        Assert.Equal(expected, FilenameParser.ParseAbsoluteEpisode(name));
    }

    [Theory]
    [InlineData("Show.S01E05.1080p")]          // season code present → season-relative
    [InlineData("Show 1x05")]                   // cross code present → season-relative
    [InlineData("Movie.2019.1080p.BluRay")]    // no absolute number
    [InlineData("[Group] Show - 2019 [1080p]")] // candidate is a year
    [InlineData("[Group] Show - 1080 [x265]")]  // candidate is a resolution height
    public void ParseAbsoluteEpisode_ReturnsNull_WhenNoValidNumber(string name)
    {
        Assert.Null(FilenameParser.ParseAbsoluteEpisode(name));
    }

    [Fact]
    public void ParseAbsoluteEpisode_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(FilenameParser.ParseAbsoluteEpisode(null!));
        Assert.Null(FilenameParser.ParseAbsoluteEpisode(""));
        Assert.Null(FilenameParser.ParseAbsoluteEpisode("   "));
    }

    // ── {AbsoluteEpisode} token rendering ───────────────────────────────────────

    private static MovieMetadata Episode(int season, int episode, string show = "One Piece")
        => new() { IsEpisode = true, ShowTitle = show, Season = season, Episode = episode };

    [Fact]
    public void AbsoluteToken_RendersWhenProvided()
    {
        var result = Svc.BuildFileName("{ShowTitle} - {AbsoluteEpisode}",
            Episode(21, 6), ".mkv", absoluteEpisode: 1086);
        Assert.Equal("One Piece - 1086.mkv", result);
    }

    [Fact]
    public void AbsoluteToken_PadWidth()
    {
        var result = Svc.BuildFileName("{ShowTitle} - {AbsoluteEpisode:000}",
            Episode(1, 5), ".mkv", absoluteEpisode: 5);
        Assert.Equal("One Piece - 005.mkv", result);
    }

    [Fact]
    public void AbsoluteToken_EmptyWhenNull_DefaultPadIsTwo()
    {
        // No absolute provided → token resolves empty; trailing " - " trimmed.
        var result = Svc.BuildFileName("{ShowTitle} - {AbsoluteEpisode}",
            Episode(1, 5), ".mkv", absoluteEpisode: null);
        Assert.Equal("One Piece.mkv", result);
    }

    [Fact]
    public void AbsoluteToken_InConditionalBlock_DropsWhenEmpty()
    {
        var result = Svc.BuildFileName("{ShowTitle} S{Season}E{Episode}< - {AbsoluteEpisode}>",
            Episode(1, 5), ".mkv", absoluteEpisode: null);
        Assert.Equal("One Piece S01E05.mkv", result);
    }

    [Fact]
    public void AbsoluteToken_InConditionalBlock_KeptWhenPresent()
    {
        var result = Svc.BuildFileName("{ShowTitle} S{Season}E{Episode}< - {AbsoluteEpisode}>",
            Episode(1, 5), ".mkv", absoluteEpisode: 130);
        Assert.Equal("One Piece S01E05 - 130.mkv", result);
    }

    [Fact]
    public void AbsoluteToken_CoexistsWithEpisodeRange()
    {
        // episodeEnd and absoluteEpisode are independent parameters.
        var result = Svc.BuildFileName("S{Season}E{Episode} (abs {AbsoluteEpisode})",
            Episode(1, 1), ".mkv", episodeEnd: 3, absoluteEpisode: 25);
        Assert.Equal("S01E01-03 (abs 25).mkv", result);
    }

    [Fact]
    public void Legacy_NoAbsoluteToken_Unchanged()
    {
        // Patterns without {AbsoluteEpisode} are unaffected by the new parameter.
        var result = Svc.BuildFileName("{ShowTitle} - S{Season}E{Episode}",
            Episode(2, 5), ".mkv", absoluteEpisode: 999);
        Assert.Equal("One Piece - S02E05.mkv", result);
    }
}
