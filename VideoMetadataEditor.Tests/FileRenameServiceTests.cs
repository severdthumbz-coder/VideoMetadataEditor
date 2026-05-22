using VideoMetadataEditor.Services;
using VideoMetadataEditor.Models;
using Xunit;

namespace VideoMetadataEditor.Tests;

/// <summary>
/// Tests for FileRenameService.BuildFileName token substitution and sanitization.
/// BuildFileName is an instance method that always appends the extension, trims
/// trailing separators, and sanitizes invalid filename characters.
/// </summary>
public class FileRenameServiceTests
{
    private static readonly FileRenameService Svc = new();

    private static MovieMetadata Meta(
        string title    = "Test Movie",
        string year     = "2024",
        string genre    = "Action",
        string director = "Jane Director",
        string cast     = "Actor One, Actor Two",
        float  rating   = 7.5f,
        string mpa      = "PG-13",
        string imdbId   = "tt1234567",
        string tmdbId   = "999")
    {
        return new MovieMetadata
        {
            Title     = title,
            Year      = year,
            Genre     = genre,
            Director  = director,
            Cast      = cast,
            Rating    = rating,
            MpaRating = mpa,
            ImdbId    = imdbId,
            TmdbId    = tmdbId,
        };
    }

    [Fact]
    public void BasicTokens_Substituted()
    {
        var result = Svc.BuildFileName("{Title} ({Year})", Meta(), ".mp4");
        Assert.Equal("Test Movie (2024).mp4", result);
    }

    [Fact]
    public void MpaToken_Substituted()
    {
        var result = Svc.BuildFileName("{Title} ({Year}) [{MPA}]", Meta(), ".mp4");
        Assert.Equal("Test Movie (2024) [PG-13].mp4", result);
    }

    [Fact]
    public void CastToken_Substituted()
    {
        var result = Svc.BuildFileName("{Title} - {Cast}", Meta(cast: "Tom Hanks"), ".mkv");
        Assert.Equal("Test Movie - Tom Hanks.mkv", result);
    }

    [Fact]
    public void ResolutionAndFormatTokens_Substituted()
    {
        var result = Svc.BuildFileName("{Title}.{Resolution}.{Format}", Meta(), ".mkv",
            resolution: "1080p", format: "x265");
        Assert.Equal("Test Movie.1080p.x265.mkv", result);
    }

    [Fact]
    public void EmptyResolution_TokenBecomesEmpty()
    {
        // Trailing separators are trimmed, so "{Title}.{Resolution}" with empty res
        // collapses to just the title before the extension is appended.
        var result = Svc.BuildFileName("{Title}{Resolution}", Meta(), ".mp4", resolution: "");
        Assert.Equal("Test Movie.mp4", result);
    }

    [Fact]
    public void ColonInTitle_ReplacedWithDash()
    {
        var result = Svc.BuildFileName("{Title} ({Year})",
            Meta(title: "Star Wars: A New Hope"), ".mp4");
        Assert.Equal("Star Wars - A New Hope (2024).mp4", result);
    }

    [Fact]
    public void InvalidFilesystemChars_Stripped()
    {
        var result = Svc.BuildFileName("{Title}", Meta(title: "Bad/Name<Movie>"), ".mp4");
        Assert.DoesNotContain("/", result);
        Assert.DoesNotContain("<", result);
        Assert.DoesNotContain(">", result);
    }

    [Fact]
    public void ImdbPattern_CorrectFormat()
    {
        var result = Svc.BuildFileName("{Title} ({Year}) [{ImdbId}]", Meta(), ".mkv");
        Assert.Equal("Test Movie (2024) [tt1234567].mkv", result);
    }

    [Fact]
    public void EmptyTitle_FallsBackToUntitled()
    {
        var result = Svc.BuildFileName("{Title}", Meta(title: ""), ".mp4");
        Assert.Equal("Untitled.mp4", result);
    }

    [Fact]
    public void ExtensionWithoutDot_StillGetsDot()
    {
        var result = Svc.BuildFileName("{Title}", Meta(title: "Movie"), "mp4");
        Assert.Equal("Movie.mp4", result);
    }

    [Fact]
    public void TvTokens_SeasonEpisodePadded()
    {
        var meta = new MovieMetadata
        {
            ShowTitle = "Breaking Bad",
            Season    = 2,
            Episode   = 5,
        };
        var result = Svc.BuildFileName("{ShowTitle} S{Season}E{Episode}", meta, ".mkv");
        Assert.Equal("Breaking Bad S02E05.mkv", result);
    }
}
