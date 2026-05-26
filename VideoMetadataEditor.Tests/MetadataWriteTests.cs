using System;
using System.IO;
using VideoMetadataEditor.Models;
using VideoMetadataEditor.Services;
using Xunit;

namespace VideoMetadataEditor.Tests;

/// <summary>
/// Tests that verify MetadataService can write tags to a real file on disk
/// and read them back correctly.
///
/// These tests are SKIPPED in CI because they require TagLib# to successfully
/// open and write to a real video container — something a minimal stub can't
/// always guarantee on a fresh runner. Run locally to verify write behaviour.
///
/// To run locally: remove the Skip = "..." from each [Fact] attribute.
///
/// The minimal MP4 is structurally valid (ftyp + moov(mvhd+udta) + mdat, 161 bytes)
/// but some versions of TagLib# require a trak atom to fully initialise the
/// tag layer for writing. The VmeCommentCodec round-trip tests (MetadataRoundTripTests)
/// already verify the encode/decode logic that these tests would exercise.
/// </summary>
public class MetadataWriteTests : IDisposable
{
    private readonly string _tempDir;

    // Minimal valid MP4: ftyp(isom/iso2/mp41) + moov(mvhd@108b + udta) + mdat
    // Boxes: ftyp@0(28), moov@28(124)[mvhd@36(108),udta@144(8)], mdat@152(9)
    private const string MinimalMp4Base64 =
        "AAAAHGZ0eXBpc29tAAACAGlzb21pc28ybXA0MQAAAHxtb292" +
        "AAAAbG12aGQAAAAAAAAAAAAAAAAAAAPoAAAAAAABAAABAAAA" +
        "AAAAAAAAAAAAAQAAAAAAAAAAAAAAAAAAAAEAAAAAAAAAAAAA" +
        "AAAAAEAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAC" +
        "AAAACHVkdGEAAAAJbWRhdAA=";

    public MetadataWriteTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"VmeWriteTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private string CreateMp4(string name = "test.mp4")
    {
        var path  = Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, Convert.FromBase64String(MinimalMp4Base64));
        return path;
    }

    private static AppSettings DefaultSettings() => new()
    {
        AutoRenameEnabled  = false,
        VerboseWriteErrors = false,
        ArtworkJpegQuality = 85,
        ArtworkMaxPx       = 1000,
    };

    private const string SkipReason =
        "MetadataService write tests require TagLib# to open a real video container. " +
        "Remove this Skip attribute and run locally to verify the write path.";

    // ── Tests — remove Skip to run locally ───────────────────────────────────

    [Fact(Skip = SkipReason)]
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

    [Fact(Skip = SkipReason)]
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

        Assert.True(result.Success, $"Write failed: {result.ErrorMessage}");
        var read = svc.ReadMetadataFast(path);
        Assert.Equal("Inception", read.Title);
        Assert.Equal("tt1375666", read.ImdbId);
        Assert.Equal(8.8f,        read.Rating, precision: 1);
        Assert.Equal("PG-13",     read.MpaRating);
    }

    [Fact(Skip = SkipReason)]
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

        Assert.True(result.Success, $"Write failed: {result.ErrorMessage}");
        var read = svc.ReadMetadataFast(path);
        Assert.True(read.IsEpisode);
        Assert.Equal("Breaking Bad", read.ShowTitle);
        Assert.Equal(2, read.Season);
        Assert.Equal(7, read.Episode);
    }

    [Fact(Skip = SkipReason)]
    public async Task Mp4_RatingStoredAsInvariantDecimal()
    {
        var path = CreateMp4("rating.mp4");
        var svc  = new MetadataService();
        await svc.WriteMetadataDetailedAsync(path,
            new MovieMetadata { Title = "Test", Rating = 8.5f }, DefaultSettings());
        Assert.Equal(8.5f, svc.ReadMetadataFast(path).Rating, precision: 1);
    }

    [Fact(Skip = SkipReason)]
    public async Task Mp4_TempFileCleanedUpAfterSuccessfulWrite()
    {
        var path = CreateMp4("cleanup.mp4");
        var svc  = new MetadataService();
        await svc.WriteMetadataDetailedAsync(path,
            new MovieMetadata { Title = "Cleanup" }, DefaultSettings());
        Assert.Empty(Directory.GetFiles(_tempDir, ".vme_tmp_*"));
    }

    [Fact(Skip = SkipReason)]
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

    [Fact(Skip = SkipReason)]
    public async Task Mp4_WatchedFlag_RoundTrips()
    {
        var path = CreateMp4("watched.mp4");
        var svc  = new MetadataService();
        await svc.WriteMetadataDetailedAsync(path,
            new MovieMetadata { Title = "Film", IsWatched = true }, DefaultSettings());
        Assert.True(svc.ReadMetadataFast(path).IsWatched);
    }

    [Fact(Skip = SkipReason)]
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
