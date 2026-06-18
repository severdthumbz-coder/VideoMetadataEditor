using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VideoMetadataEditor.Models;
using VideoMetadataEditor.Services;
using Xunit;

namespace VideoMetadataEditor.Tests;

/// <summary>
/// Integration tests for the library cache persistence cycle — the layer where the
/// recurring "metadata reverts after a scan / app restart" bug actually lived.
///
/// Unlike <see cref="MetadataWriteTests"/>, these do NOT require TagLib# to open a
/// real container: the cache is pure logic over a JSON file, so they run unskipped
/// in CI. They simulate the exact lifecycle that exposed the revert bug:
///
///     embed → Put(...) → SaveAsync(...)            [session 1]
///     ──────────────── app close / reopen ────────────────
///     new LibraryCacheService → Load(...) → TryGet(...)   [session 2]
///
/// If a future change ever reintroduces a path where an embed's metadata fails to
/// survive a save+reload, one of these tests fails instead of the user finding out.
/// </summary>
public class LibraryCachePersistenceTests : IDisposable
{
    private readonly string _folder;

    public LibraryCachePersistenceTests()
    {
        // A unique "library folder" — its path hashes to a unique cache file name,
        // so concurrent test runs never collide on the shared BaseDirectory.
        _folder = Path.Combine(Path.GetTempPath(), $"VmeCacheTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        try
        {
            // Clean up both the temp folder and the cache file this folder maps to.
            new LibraryCacheService().Clear(_folder);
            if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
        }
        catch { /* best effort */ }
    }

    private static LibraryEntry MakeEntry(string path) => new()
    {
        FilePath      = path,
        FileSizeBytes = 123_456,
        Title         = "Inception",
        Year          = "2010",
        Genre         = "Sci-Fi",
        Director      = "Christopher Nolan",
        Cast          = "Leonardo DiCaprio",
        Description   = "A thief who steals corporate secrets.",
        ImdbId        = "tt1375666",
        TmdbId        = "27205",
        ImdbRating    = 8.8f,
        MpaRating     = "PG-13",
        IsWatched     = true,
    };

    private static LibraryEntry MakeEpisode(string path) => new()
    {
        FilePath      = path,
        FileSizeBytes = 555_000,
        Title         = "Stone Wars",
        IsEpisode     = true,
        ShowTitle     = "Dr. Stone",
        Season        = 2,
        Episode       = 4,
        EpisodeTitle  = "Stone Wars",
        AiredDate     = "2021-02-04",
        TmdbSeriesId  = "86031",
    };

    // ── The core regression: metadata must survive Put → Save → reload ─────────

    [Fact]
    public async Task EmbeddedMovie_SurvivesSaveAndReloadInNewInstance()
    {
        var file  = Path.Combine(_folder, "Inception.mp4");
        var size  = 123_456L;
        var mtime = DateTime.UtcNow;

        // Session 1: embed updates the cache, then SaveAsync persists it to disk.
        var session1 = new LibraryCacheService();
        session1.Load(_folder);                       // sets the current cache path
        session1.Put(file, size, mtime, MakeEntry(file));
        await session1.SaveAsync(new[] { file });

        // Session 2: a brand-new instance, as if the app was closed and reopened.
        var session2 = new LibraryCacheService();
        session2.Load(_folder);
        var got = session2.TryGet(file, size, mtime);

        Assert.NotNull(got);
        Assert.Equal("Inception",         got!.Title);
        Assert.Equal("2010",              got.Year);
        Assert.Equal("Christopher Nolan", got.Director);
        Assert.Equal("tt1375666",         got.ImdbId);
        Assert.Equal(8.8f,                got.ImdbRating, precision: 1);
        Assert.Equal("PG-13",             got.MpaRating);
        Assert.True(got.IsWatched);
    }

    [Fact]
    public async Task EmbeddedEpisode_TvFieldsSurviveReload()
    {
        // Directly guards the "Dr. Stone S02E04 shows as untagged" symptom: if the
        // episode fields don't round-trip, the reloaded entry loses IsEpisode and the
        // TV tree re-flags it as untagged.
        var file  = Path.Combine(_folder, "Dr.Stone.S02E04.mkv");
        var size  = 555_000L;
        var mtime = DateTime.UtcNow;

        var session1 = new LibraryCacheService();
        session1.Load(_folder);
        session1.Put(file, size, mtime, MakeEpisode(file));
        await session1.SaveAsync(new[] { file });

        var session2 = new LibraryCacheService();
        session2.Load(_folder);
        var got = session2.TryGet(file, size, mtime);

        Assert.NotNull(got);
        Assert.True(got!.IsEpisode);
        Assert.Equal("Dr. Stone", got.ShowTitle);
        Assert.Equal(2, got.Season);
        Assert.Equal(4, got.Episode);
        Assert.Equal("Stone Wars", got.EpisodeTitle);
    }

    // ── Freshness invalidation: a modified file must NOT return stale cache ────

    [Fact]
    public async Task ModifiedFile_ReturnsCacheMiss_NotStaleData()
    {
        var file = Path.Combine(_folder, "movie.mp4");

        var session1 = new LibraryCacheService();
        session1.Load(_folder);
        session1.Put(file, 1000, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), MakeEntry(file));
        await session1.SaveAsync(new[] { file });

        var session2 = new LibraryCacheService();
        session2.Load(_folder);

        // Same path, but the file was modified (newer mtime, different size) →
        // the cache must report a miss so the scan does a fresh disk read.
        var stale = session2.TryGet(file, 2000, new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Null(stale);
    }

    // ── Rename handling: old path is pruned, new path persists ────────────────

    [Fact]
    public async Task RenamedFile_OldPathEvicted_NewPathSurvives()
    {
        var oldPath = Path.Combine(_folder, "old name.mp4");
        var newPath = Path.Combine(_folder, "New Name (2010).mp4");
        var size    = 123_456L;
        var mtime   = DateTime.UtcNow;

        var session1 = new LibraryCacheService();
        session1.Load(_folder);
        session1.Put(oldPath, size, mtime, MakeEntry(oldPath));
        // Simulate the rename path in SyncLibraryEntry: evict old, put new.
        session1.Evict(oldPath);
        session1.Put(newPath, size, mtime, MakeEntry(newPath));
        // Only the new path is "live" — SaveAsync prunes the rest.
        await session1.SaveAsync(new[] { newPath });

        var session2 = new LibraryCacheService();
        session2.Load(_folder);

        Assert.Null(session2.TryGet(oldPath, size, mtime));      // gone
        Assert.NotNull(session2.TryGet(newPath, size, mtime));   // survives
    }

    // ── Ghost pruning: files no longer on disk are dropped on save ────────────

    [Fact]
    public async Task SaveAsync_PrunesPathsNotInLiveSet()
    {
        var keep = Path.Combine(_folder, "keep.mp4");
        var gone = Path.Combine(_folder, "deleted.mp4");
        var size  = 100L;
        var mtime = DateTime.UtcNow;

        var session1 = new LibraryCacheService();
        session1.Load(_folder);
        session1.Put(keep, size, mtime, MakeEntry(keep));
        session1.Put(gone, size, mtime, MakeEntry(gone));
        // Only "keep" still exists on disk.
        await session1.SaveAsync(new[] { keep });

        var session2 = new LibraryCacheService();
        session2.Load(_folder);

        Assert.NotNull(session2.TryGet(keep, size, mtime));
        Assert.Null(session2.TryGet(gone, size, mtime));
    }
}
