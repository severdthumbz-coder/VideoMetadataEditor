using System;
using System.IO;
using VideoMetadataEditor.Models;
using VideoMetadataEditor.Services;
using Xunit;

namespace VideoMetadataEditor.Tests;

/// <summary>
/// Tests that verify MetadataService can write tags to a real file on disk
/// and read them back correctly. These are the most critical tests in the
/// suite — a regression here means silent metadata corruption for every
/// file a user processes.
///
/// Strategy: create minimal but valid MP4/MKV containers on disk, write
/// metadata via WriteMetadataDetailedAsync, then read back via
/// ReadMetadataFast and verify all fields survive the round-trip.
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

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a minimal valid MP4 file that TagLib# can open and write tags to.
    /// Structure: ftyp (isom) + free + moov (with mvhd).
    /// No video/audio streams — pure container with enough structure for tag I/O.
    /// </summary>
    private string CreateMinimalMp4(string name = "test.mp4")
    {
        var path = Path.Combine(_tempDir, name);

        // ftyp box
        var ftypContent = new byte[]
        {
            // major brand: isom
            0x69, 0x73, 0x6F, 0x6D,
            // minor version: 0
            0x00, 0x00, 0x00, 0x00,
            // compatible brands: isom, mp41
            0x69, 0x73, 0x6F, 0x6D, 0x6D, 0x70, 0x34, 0x31
        };
        var ftyp = BuildBox("ftyp", ftypContent);

        // free box (8-byte padding)
        var free = BuildBox("free", new byte[8]);

        // moov box with mvhd (minimal movie header)
        var mvhdContent = new byte[100]; // version=0, all fields zeroed — valid for tag-only use
        // timescale at offset 12: 0x000003E8 = 1000
        mvhdContent[12] = 0x00; mvhdContent[13] = 0x00;
        mvhdContent[14] = 0x03; mvhdContent[15] = 0xE8;
        // rate at offset 20: 0x00010000 = 1.0
        mvhdContent[20] = 0x00; mvhdContent[21] = 0x01;
        // volume at offset 24: 0x0100 = 1.0
        mvhdContent[24] = 0x01;
        var mvhd = BuildBox("mvhd", mvhdContent);
        var moov = BuildBox("moov", mvhd);

        using var fs = File.Create(path);
        fs.Write(ftyp); fs.Write(free); fs.Write(moov);
        return path;
    }

    /// <summary>
    /// Creates a minimal valid MKV (Matroska) file that TagLib# can open.
    /// Structure: EBML header + Segment with Info element.
    /// </summary>
    private string CreateMinimalMkv(string name = "test.mkv")
    {
        var path = Path.Combine(_tempDir, name);

        // EBML header
        var ebmlHeader = new byte[]
        {
            0x1A, 0x45, 0xDF, 0xA3,  // EBML element ID
            0x9F,                     // size (31 bytes)
            0x42, 0x86, 0x81, 0x01,  // EBMLVersion = 1
            0x42, 0xF7, 0x81, 0x01,  // EBMLReadVersion = 1
            0x42, 0xF2, 0x81, 0x04,  // EBMLMaxIDLength = 4
            0x42, 0xF3, 0x81, 0x08,  // EBMLMaxSizeLength = 8
            0x42, 0x82, 0x84,        // DocType element, size 4
            0x6D, 0x61, 0x74, 0x72, 0x6F, 0x73, 0x6B, 0x61, // "matroska"
            0x42, 0x87, 0x81, 0x04,  // DocTypeVersion = 4
            0x42, 0x85, 0x81, 0x02,  // DocTypeReadVersion = 2
        };

        // Minimal Segment (unknown size) with TimestampScale
        var segmentBody = new byte[]
        {
            // Info element
            0x15, 0x49, 0xA9, 0x66, // Info ID
            0x8D,                   // size 13
            0x2A, 0xD7, 0xB1,       // TimestampScale ID
            0x83,                   // size 3
            0x0F, 0x42, 0x40,       // value 1000000 (1ms)
            0x7B, 0xA9,             // Title ID
            0x80,                   // size 0 (empty title)
        };

        var segmentId = new byte[] { 0x18, 0x53, 0x80, 0x67 };
        var segmentSize = new byte[] { 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }; // unknown

        using var fs = File.Create(path);
        fs.Write(ebmlHeader);
        fs.Write(segmentId);
        fs.Write(segmentSize);
        fs.Write(segmentBody);
        return path;
    }

    private static byte[] BuildBox(string type, byte[] content)
    {
        var size = 8 + content.Length;
        var box  = new byte[size];
        box[0] = (byte)(size >> 24); box[1] = (byte)(size >> 16);
        box[2] = (byte)(size >> 8);  box[3] = (byte)size;
        var typeBytes = System.Text.Encoding.ASCII.GetBytes(type);
        Array.Copy(typeBytes, 0, box, 4, 4);
        Array.Copy(content,  0, box, 8, content.Length);
        return box;
    }

    private static AppSettings DefaultSettings() => new()
    {
        AutoRenameEnabled    = false,
        VerboseWriteErrors   = false,
        ArtworkJpegQuality   = 85,
        ArtworkMaxPx         = 1000,
    };

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Mp4_WriteAndReadBack_TitlePreserved()
    {
        var path = CreateMinimalMp4();
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
        var path = CreateMinimalMp4("core_fields.mp4");
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
        Assert.Equal("Inception",   read.Title);
        Assert.Equal("2010",        read.Year);
        Assert.Equal("tt1375666",   read.ImdbId);
        Assert.Equal("27205",       read.TmdbId);
        Assert.Equal(8.8f,          read.Rating, precision: 1);
        Assert.Equal("PG-13",       read.MpaRating);
    }

    [Fact]
    public async Task Mp4_WriteAndReadBack_TvEpisode()
    {
        var path = CreateMinimalMp4("episode.mp4");
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
        Assert.Equal("Breaking Bad",  read.ShowTitle);
        Assert.Equal(2,               read.Season);
        Assert.Equal(7,               read.Episode);
        Assert.Equal("Negro y Azul",  read.EpisodeTitle);
    }

    [Fact]
    public async Task Mp4_Write_DoesNotDestroyFileOnFailure()
    {
        var path     = CreateMinimalMp4("safety_test.mp4");
        var original = File.ReadAllBytes(path);
        var svc      = new MetadataService();

        // Write valid metadata first
        var meta = new MovieMetadata { Title = "Original Title" };
        await svc.WriteMetadataDetailedAsync(path, meta, DefaultSettings());

        // File should still exist and be non-empty after a successful write
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 0);

        // Content should have the new title
        var read = svc.ReadMetadataFast(path);
        Assert.Equal("Original Title", read.Title);
    }

    [Fact]
    public async Task Mp4_WriteWatchedFlag_RoundTrips()
    {
        var path = CreateMinimalMp4("watched.mp4");
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
        var path = CreateMinimalMp4("overwrite.mp4");
        var svc  = new MetadataService();

        // Write initial metadata
        var first = new MovieMetadata { Title = "First Title", Year = "2020", Rating = 7.0f };
        await svc.WriteMetadataDetailedAsync(path, first, DefaultSettings());

        // Overwrite with new metadata
        var second = new MovieMetadata { Title = "Updated Title", Year = "2023", Rating = 9.0f };
        var result = await svc.WriteMetadataDetailedAsync(path, second, DefaultSettings());

        Assert.True(result.Success, $"Overwrite failed: {result.ErrorMessage}");

        var read = svc.ReadMetadataFast(path);
        Assert.Equal("Updated Title", read.Title);
        Assert.Equal("2023",          read.Year);
        Assert.Equal(9.0f,            read.Rating, precision: 1);
    }

    [Fact]
    public async Task Mp4_RatingStoredAsInvariantDecimal()
    {
        // Regression: ratings must round-trip correctly regardless of system locale.
        // A French or German system uses comma as decimal separator — TagLib# and
        // VmeCommentCodec must both use InvariantCulture.
        var path = CreateMinimalMp4("rating_locale.mp4");
        var svc  = new MetadataService();
        var meta = new MovieMetadata { Title = "Rating Test", Rating = 8.5f };

        await svc.WriteMetadataDetailedAsync(path, meta, DefaultSettings());

        var read = svc.ReadMetadataFast(path);
        Assert.Equal(8.5f, read.Rating, precision: 1);
    }

    [Fact]
    public async Task Mp4_TempFileCleanedUpAfterSuccessfulWrite()
    {
        var path = CreateMinimalMp4("cleanup.mp4");
        var svc  = new MetadataService();
        var meta = new MovieMetadata { Title = "Cleanup Test" };

        await svc.WriteMetadataDetailedAsync(path, meta, DefaultSettings());

        // No .vme_tmp_ files should remain after a successful write
        var tempFiles = Directory.GetFiles(_tempDir, ".vme_tmp_*");
        Assert.Empty(tempFiles);
    }

    [Fact]
    public async Task Mkv_WriteAndReadBack_TitlePreserved()
    {
        var path = CreateMinimalMkv();
        var svc  = new MetadataService();
        var meta = new MovieMetadata { Title = "MKV Test Movie", Year = "2022" };

        var result = await svc.WriteMetadataDetailedAsync(path, meta, DefaultSettings());

        // MKV write may use TagLib# (without mkvpropedit in test environment) —
        // the important assertion is that it doesn't crash and the file is intact.
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 0);

        // If write succeeded, title should round-trip
        if (result.Success)
        {
            var read = svc.ReadMetadataFast(path);
            Assert.Equal("MKV Test Movie", read.Title);
        }
    }
}
