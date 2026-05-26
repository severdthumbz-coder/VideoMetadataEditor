using System;
using System.IO;
using VideoMetadataEditor.Models;
using VideoMetadataEditor.Services;
using Xunit;

namespace VideoMetadataEditor.Tests;

/// <summary>
/// Tests that verify MetadataService can write tags to a real file on disk
/// and read them back correctly. Uses a known-good minimal MP4 container
/// (embedded as base64) that TagLib# can reliably open and write to.
///
/// The minimal MP4 structure:
///   ftyp (isom/iso2/mp41) + moov (mvhd + udta) + mdat
/// This is the minimum structure TagLib# requires to open an MP4 for tag writing.
/// </summary>
public class MetadataWriteTests : IDisposable
{
    private readonly string _tempDir;

    // Minimal valid MP4 that TagLib# can open and write tags to.
    // Structure: ftyp(isom) + moov(mvhd+udta) + mdat
    // Generated from a known-good binary structure (161 bytes).
    private const string MinimalMp4Base64 =
        "AAAAHGZ0eXBpc29tAAACAGlzb21pc28ybXA0MQAAAHxtb292" +
        "AAAAbG12aGQAAAAAAAAAAAAAAAAAAAPoAAAAAAABAAABAAAAAAAA" +
        "AAAAAAAQAAAAAAAAAAAAAAAAAAAEAAAAAAAAAAAAAAAAAAAAAAA" +
        "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAIAAAACHVkdGEA" +
        "AAAJbWRhdAA=";

    public MetadataWriteTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"VmeWriteTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private string CreateMp4(string name = "test.mp4")
    {
        var path  = Path.Combine(_tempDir, name);
        var bytes = Convert.FromBase64String(MinimalMp4Base64);
        File.WriteAllBytes(path, bytes);
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

        Assert.True(result.Success, $"Write failed: {result.ErrorMessage}");
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
            Title       = "Inception",
            Year        = "2010",
            Director    = "Christopher Nolan",
            Genre       = "Sci-Fi",
            Description = "A thief who steals corporate secrets.",
            ImdbId      = "tt1375666",
            TmdbId      = "27205",
            Rating      = 8.8f,
            MpaRating   = "PG-13",
        };

        var result = await svc.WriteMetadataDetailedAsync(path, meta, DefaultSettings());

        Assert.True(result.Success, $"Write failed: {result.ErrorMessage}");
        var read = svc.ReadMetadataFast(path);
        Assert.Equal("Inception", read.Title);
        Assert.Equal("2010",      read.Year);
        Assert.Equal("tt1375666", read.ImdbId);
        Assert.Equal("27205",     read.TmdbId);
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
            IsEpisode    = true,
            ShowTitle    = "Breaking Bad",
            Season       = 2,
            Episode      = 7,
            EpisodeTitle = "Negro y Azul",
            Title        = "Breaking Bad",
            Year         = "2009",
        };

        var result = await svc.WriteMetadataDetailedAsync(path, meta, DefaultSettings());

        Assert.True(result.Success, $"Write failed: {result.ErrorMessage}");
        var read = svc.ReadMetadataFast(path);
        Assert.True(read.IsEpisode);
        Assert.Equal("Breaking Bad", read.ShowTitle);
        Assert.Equal(2,              read.Season);
        Assert.Equal(7,              read.Episode);
        Assert.Equal("Negro y Azul", read.EpisodeTitle);
    }

    [Fact]
    public async Task Mp4_Write_FileExistsAndNonEmptyAfterSuccess()
    {
        var path = CreateMp4("safety_test.mp4");
        var svc  = new MetadataService();
        var meta = new MovieMetadata { Title = "Safety Test" };

        var result = await svc.WriteMetadataDetailedAsync(path, meta, DefaultSettings());

        Assert.True(result.Success, $"Write failed: {result.ErrorMessage}");
        Assert.True(File.Exists(path), "File should still exist after write");
        Assert.True(new FileInfo(path).Length > 0, "File should be non-empty after write");
    }

    [Fact]
    public async Task Mp4_WriteWatchedFlag_RoundTrips()
    {
        var path = CreateMp4("watched.mp4");
        var svc  = new MetadataService();
        var meta = new MovieMetadata { Title = "A Film", IsWatched = true };

        var result = await svc.WriteMetadataDetailedAsync(path, meta, DefaultSettings());

        Assert.True(result.Success, $"Write failed: {result.ErrorMessage}");
        var read = svc.ReadMetadataFast(path);
        Assert.True(read.IsWatched);
    }

    [Fact]
    public async Task Mp4_OverwriteExistingTags_UpdatesAllFields()
    {
        var path = CreateMp4("overwrite.mp4");
        var svc  = new MetadataService();

        // Write initial metadata
        await svc.WriteMetadataDetailedAsync(
            path, new MovieMetadata { Title = "First Title", Year = "2020", Rating = 7.0f },
            DefaultSettings());

        // Overwrite
        var result = await svc.WriteMetadataDetailedAsync(
            path, new MovieMetadata { Title = "Updated Title", Year = "2023", Rating = 9.0f },
            DefaultSettings());

        Assert.True(result.Success, $"Overwrite failed: {result.ErrorMessage}");
        var read = svc.ReadMetadataFast(path);
        Assert.Equal("Updated Title", read.Title);
        Assert.Equal("2023",          read.Year);
        Assert.Equal(9.0f,            read.Rating, precision: 1);
    }

    [Fact]
    public async Task Mp4_RatingStoredAsInvariantDecimal()
    {
        // Regression: ratings must survive regardless of system locale.
        // French/German systems use comma as decimal — InvariantCulture must be used.
        var path = CreateMp4("rating_locale.mp4");
        var svc  = new MetadataService();

        await svc.WriteMetadataDetailedAsync(
            path, new MovieMetadata { Title = "Rating Test", Rating = 8.5f },
            DefaultSettings());

        var read = svc.ReadMetadataFast(path);
        Assert.Equal(8.5f, read.Rating, precision: 1);
    }

    [Fact]
    public async Task Mp4_TempFileCleanedUpAfterSuccessfulWrite()
    {
        var path = CreateMp4("cleanup.mp4");
        var svc  = new MetadataService();

        await svc.WriteMetadataDetailedAsync(
            path, new MovieMetadata { Title = "Cleanup Test" },
            DefaultSettings());

        // No .vme_tmp_ files should remain after a successful write
        var tempFiles = Directory.GetFiles(_tempDir, ".vme_tmp_*");
        Assert.Empty(tempFiles);
    }
}
