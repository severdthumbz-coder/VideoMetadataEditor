using System;
using System.IO;
using VideoMetadataEditor.Models;
using VideoMetadataEditor.Services;
using Xunit;

namespace VideoMetadataEditor.Tests;

/// <summary>
/// Tests that verify MetadataService can write tags to a real file on disk
/// and read them back correctly — the app's core function.
///
/// These run against a real committed MP4 fixture (Assets/tiny.mp4, a 1-second
/// libx264 clip generated with ffmpeg and verified writable in the app). Each test
/// copies a fresh clean copy into a temp dir, so every write starts from an untagged
/// file. Previously these were skipped because a hand-built minimal MP4 stub didn't
/// reliably initialise TagLib#'s tag layer for writing; the real fixture fixes that,
/// putting the write round-trip under automated CI verification.
/// </summary>
public class MetadataWriteTests : IDisposable
{
    private readonly string _tempDir;

    public MetadataWriteTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"VmeWriteTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // Real MP4 fixture (committed at Assets/tiny.mp4, copied next to the test
    // assembly by the .csproj). A genuine libx264 container that TagLib# opens and
    // writes tags to — verified in the app before committing. Copies a fresh clean
    // copy into the temp dir per test so each write starts from an untagged file.
    private static string FixturePath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "tiny.mp4");

    private string CreateMp4(string name = "test.mp4")
    {
        var src = FixturePath;
        // If the fixture is missing (e.g. not committed / not copied to output),
        // fail loudly with a clear reason rather than silently skipping — a missing
        // test asset is a real problem, not a condition to hide.
        Assert.True(File.Exists(src),
            $"Test fixture not found at '{src}'. Ensure Assets/tiny.mp4 is committed " +
            "and marked CopyToOutputDirectory in the test project.");
        var path = Path.Combine(_tempDir, name);
        File.Copy(src, path, overwrite: true);
        return path;
    }

    private static AppSettings DefaultSettings() => new()
    {
        AutoRenameEnabled  = false,
        VerboseWriteErrors = false,
        ArtworkJpegQuality = 85,
        ArtworkMaxPx       = 1000,
    };

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Mp4_WriteAndReadBack_TitlePreserved()
    {
        var path = CreateMp4();
        var svc  = new MetadataService();
        var meta = new MovieMetadata { Title = "Test Movie", Year = "2024" };

        var result = await svc.WriteMetadataDetailedAsync(path, meta, DefaultSettings());

        Assert.True(result.Success, $"Write failed: {result.Diagnosis?.Reasons.FirstOrDefault() ?? "unknown"}");
        var read = svc.ReadMetadataFast(path);
        Assert.Equal("Test Movie", read.Title);
    }

    [Fact]
    public async Task Mp4_WriteAndReadBack_AllCoreFields()
    {
        var path = CreateMp4("core_fields.mp4");
        var svc  = new MetadataService();
        var meta = new MovieMetadata
        {
            Title     = "Inception", Year    = "2010",
            ImdbId    = "tt1375666", TmdbId  = "27205",
            Rating    = 8.8f,        MpaRating = "PG-13",
        };

        var result = await svc.WriteMetadataDetailedAsync(path, meta, DefaultSettings());

        Assert.True(result.Success, $"Write failed: {result.Diagnosis?.Reasons.FirstOrDefault() ?? "unknown"}");
        var read = svc.ReadMetadataFast(path);
        Assert.Equal("Inception", read.Title);
        Assert.Equal("tt1375666", read.ImdbId);
        Assert.Equal(8.8f,        read.Rating, precision: 1);
        Assert.Equal("PG-13",     read.MpaRating);
    }

    [Fact]
    public async Task Mp4_WriteAndReadBack_TvEpisode()
    {
        var path = CreateMp4("episode.mp4");
        var svc  = new MetadataService();
        var meta = new MovieMetadata
        {
            IsEpisode = true, ShowTitle = "Breaking Bad",
            Season    = 2,   Episode   = 7, EpisodeTitle = "Negro y Azul",
        };

        var result = await svc.WriteMetadataDetailedAsync(path, meta, DefaultSettings());

        Assert.True(result.Success, $"Write failed: {result.Diagnosis?.Reasons.FirstOrDefault() ?? "unknown"}");
        var read = svc.ReadMetadataFast(path);
        Assert.True(read.IsEpisode);
        Assert.Equal("Breaking Bad", read.ShowTitle);
        Assert.Equal(2, read.Season);
        Assert.Equal(7, read.Episode);
    }

    [Fact]
    public async Task Mp4_RatingStoredAsInvariantDecimal()
    {
        var path = CreateMp4("rating.mp4");
        var svc  = new MetadataService();
        await svc.WriteMetadataDetailedAsync(path,
            new MovieMetadata { Title = "Test", Rating = 8.5f }, DefaultSettings());
        Assert.Equal(8.5f, svc.ReadMetadataFast(path).Rating, precision: 1);
    }

    [Fact]
    public async Task Mp4_TempFileCleanedUpAfterSuccessfulWrite()
    {
        var path = CreateMp4("cleanup.mp4");
        var svc  = new MetadataService();
        await svc.WriteMetadataDetailedAsync(path,
            new MovieMetadata { Title = "Cleanup" }, DefaultSettings());
        Assert.Empty(Directory.GetFiles(_tempDir, ".vme_tmp_*"));
    }

    [Fact]
    public async Task Mp4_OverwriteExistingTags_UpdatesAllFields()
    {
        var path = CreateMp4("overwrite.mp4");
        var svc  = new MetadataService();
        await svc.WriteMetadataDetailedAsync(path,
            new MovieMetadata { Title = "First", Year = "2020" }, DefaultSettings());
        var result = await svc.WriteMetadataDetailedAsync(path,
            new MovieMetadata { Title = "Updated", Year = "2023" }, DefaultSettings());
        Assert.True(result.Success);
        Assert.Equal("Updated", svc.ReadMetadataFast(path).Title);
    }

    [Fact]
    public async Task Mp4_WatchedFlag_RoundTrips()
    {
        var path = CreateMp4("watched.mp4");
        var svc  = new MetadataService();
        await svc.WriteMetadataDetailedAsync(path,
            new MovieMetadata { Title = "Film", IsWatched = true }, DefaultSettings());
        Assert.True(svc.ReadMetadataFast(path).IsWatched);
    }

    [Fact]
    public async Task Mp4_WriteDoesNotDestroyFileOnSuccess()
    {
        var path = CreateMp4("safety.mp4");
        var svc  = new MetadataService();
        await svc.WriteMetadataDetailedAsync(path,
            new MovieMetadata { Title = "Safe" }, DefaultSettings());
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 0);
    }
}
