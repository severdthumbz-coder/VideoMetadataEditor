using VideoMetadataEditor.Services;
using VideoMetadataEditor.Models;
using Xunit;

namespace VideoMetadataEditor.Tests;

/// <summary>
/// Tests for FileRenameService.Apply token substitution.
/// Uses a simple metadata stub (anonymous object values passed as params).
/// </summary>
public class FileRenameServiceTests
{
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
            Title    = title,
            Year     = year,
            Genre    = genre,
            Director = director,
            Cast     = cast,
            Rating   = rating,
            MpaRating = mpa,
            ImdbId   = imdbId,
            TmdbId   = tmdbId,
        };
    }

    [Fact]
    public void Apply_AllBasicTokens_Substituted()
    {
        var result = FileRenameService.Apply("{Title} ({Year})", Meta());
        Assert.Equal("Test Movie (2024)", result);
    }

    [Fact]
    public void Apply_MpaToken_Substituted()
    {
        var result = FileRenameService.Apply("{Title} ({Year}) [{MPA}]", Meta());
        Assert.Equal("Test Movie (2024) [PG-13]", result);
    }

    [Fact]
    public void Apply_CastToken_Substituted()
    {
        var result = FileRenameService.Apply("{Title} - {Cast}", Meta(cast: "Tom Hanks"));
        Assert.Equal("Test Movie - Tom Hanks", result);
    }

    [Fact]
    public void Apply_ResolutionAndFormatTokens_Substituted()
    {
        var result = FileRenameService.Apply("{Title}.{Resolution}.{Format}", Meta(),
            resolution: "1920×1080", format: "MKV");
        Assert.Equal("Test Movie.1920×1080.MKV", result);
    }

    [Fact]
    public void Apply_EmptyResolution_TokenBecomesEmpty()
    {
        var result = FileRenameService.Apply("{Title}.{Resolution}", Meta(), resolution: "");
        Assert.Equal("Test Movie.", result);
    }

    [Fact]
    public void Apply_ColonInTitle_ReplacedWithDash()
    {
        var result = FileRenameService.Apply("{Title} ({Year})", Meta(title: "Star Wars: A New Hope"));
        Assert.Equal("Star Wars - A New Hope (2024)", result);
    }

    [Fact]
    public void Apply_InvalidFilesystemChars_Stripped()
    {
        var result = FileRenameService.Apply("{Title}", Meta(title: "Bad/Name<Movie>"));
        // Should strip < > / etc.
        Assert.DoesNotContain("/", result);
        Assert.DoesNotContain("<", result);
        Assert.DoesNotContain(">", result);
    }

    [Fact]
    public void Apply_EmptyMpa_TokenBecomesEmpty()
    {
        var result = FileRenameService.Apply("{Title} [{MPA}]", Meta(mpa: ""));
        Assert.Equal("Test Movie []", result);
    }

    [Fact]
    public void Apply_ImdbPattern_CorrectFormat()
    {
        var result = FileRenameService.Apply("{Title} ({Year}) [{ImdbId}]", Meta());
        Assert.Equal("Test Movie (2024) [tt1234567]", result);
    }
}
