using System;
using System.IO;
using System.Threading.Tasks;
using VideoMetadataEditor.Models;
using VideoMetadataEditor.Services;
using Xunit;

namespace VideoMetadataEditor.Tests;

/// <summary>
/// Build 176 (part 1): watched files are only read once they've finished arriving, and the
/// library cache keeps every library folder (it used to keep only the last one scanned).
/// </summary>
public class FileReadinessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vme-ready-" + Guid.NewGuid().ToString("N"));
    public FileReadinessTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void NotReady_WhileSomeoneIsWritingIt()
    {
        var path = Path.Combine(_dir, "arriving.mp4");
        using (var writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            writer.Write(new byte[1024]);
            writer.Flush();
            Assert.False(FileReadiness.IsReadyNow(path));   // copy still in progress
        }
        Assert.True(FileReadiness.IsReadyNow(path));        // writer closed — done
    }

    [Fact]
    public void EmptyOrMissing_IsNotReady()
    {
        var empty = Path.Combine(_dir, "empty.mp4");
        File.WriteAllBytes(empty, Array.Empty<byte>());
        Assert.False(FileReadiness.IsReadyNow(empty));
        Assert.False(FileReadiness.IsReadyNow(Path.Combine(_dir, "nope.mp4")));
    }

    [Fact]
    public async Task Waits_UntilTheCopyFinishes_AndSaysSoOnce()
    {
        var path = Path.Combine(_dir, "copying.mkv");
        var writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        writer.Write(new byte[4096]); writer.Flush();

        int waitingCalls = 0;
        var wait = FileReadiness.WaitUntilReadyAsync(path, TimeSpan.FromSeconds(20),
            TimeSpan.FromMilliseconds(100), () => waitingCalls++);

        // Keep "copying" for a bit, growing the file, then finish.
        for (int i = 0; i < 5; i++) { await Task.Delay(100); writer.Write(new byte[4096]); writer.Flush(); }
        Assert.False(wait.IsCompleted);
        writer.Dispose();

        Assert.True(await wait);
        Assert.Equal(1, waitingCalls);
    }

    [Fact]
    public async Task GivesUp_WhenTheFileDisappears_OrNeverFinishes()
    {
        Assert.False(await FileReadiness.WaitUntilReadyAsync(Path.Combine(_dir, "gone.mp4"),
            TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(50)));

        var path = Path.Combine(_dir, "stuck.mp4");
        using var writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        writer.Write(new byte[10]); writer.Flush();
        Assert.False(await FileReadiness.WaitUntilReadyAsync(path,
            TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(100)));
    }

    [Fact]
    public async Task ReadyFile_ReturnsAfterOneSettleCheck()
    {
        var path = Path.Combine(_dir, "done.mp4");
        File.WriteAllBytes(path, new byte[100]);
        int waiting = 0;
        Assert.True(await FileReadiness.WaitUntilReadyAsync(path, TimeSpan.FromSeconds(5),
            TimeSpan.FromMilliseconds(50), () => waiting++));
        Assert.Equal(0, waiting);
    }
}

public class LibraryCacheMultiFolderTests : IDisposable
{
    private readonly string _movies = Path.Combine(Path.GetTempPath(), $"VmeMovies_{Guid.NewGuid():N}");
    private readonly string _tv     = Path.Combine(Path.GetTempPath(), $"VmeTv_{Guid.NewGuid():N}");

    public LibraryCacheMultiFolderTests()
    {
        Directory.CreateDirectory(_movies);
        Directory.CreateDirectory(_tv);
    }

    public void Dispose()
    {
        var c = new LibraryCacheService();
        c.Clear(_movies); c.Clear(_tv);
        try { Directory.Delete(_movies, true); } catch { }
        try { Directory.Delete(_tv, true); } catch { }
    }

    private static LibraryEntry E(string path, string title) => new() { FilePath = path, Title = title };

    [Fact]
    public async Task BothFolders_StayLoaded_AndBothPersist()
    {
        var movie = Path.Combine(_movies, "Inception (2010).mkv");
        var ep    = Path.Combine(_tv, "Show", "Season 01", "Show - S01E01.mkv");
        var t     = DateTime.UtcNow;

        var s1 = new LibraryCacheService();
        s1.LoadIfNeeded(_movies);
        s1.Put(movie, 10, t, E(movie, "Inception"));
        s1.LoadIfNeeded(_tv);                          // used to throw the Movies index away
        s1.Put(ep, 20, t, E(ep, "Pilot"));

        Assert.Equal("Inception", s1.TryGet(movie, 10, t)!.Title);
        Assert.Equal("Pilot", s1.TryGet(ep, 20, t)!.Title);
        Assert.True(s1.UpdateTracks(movie, "a:en:aac"));   // build 175 track info for the FIRST folder
        Assert.Equal(2, s1.CachedEntryCount);

        await s1.SaveAsync(new[] { movie, ep });

        var s2 = new LibraryCacheService();
        s2.Load(_movies); s2.Load(_tv);
        Assert.Equal("a:en:aac", s2.TryGet(movie, 10, t)!.Tracks);
        Assert.Equal("Pilot", s2.TryGet(ep, 20, t)!.Title);
    }

    [Fact]
    public async Task SavingOneFolder_NeverEmptiesTheOther()
    {
        var movie = Path.Combine(_movies, "A.mkv");
        var ep    = Path.Combine(_tv, "B.mkv");
        var t     = DateTime.UtcNow;

        var s1 = new LibraryCacheService();
        s1.Load(_movies); s1.Load(_tv);
        s1.Put(movie, 1, t, E(movie, "A"));
        s1.Put(ep, 2, t, E(ep, "B"));
        await s1.SaveAsync(new[] { movie, ep });

        // A scan of Movies alone saves with only Movies' files — TV must survive.
        await s1.SaveAsync(new[] { movie });
        // A save while the TV drive is offline (none of its files listed) — TV must survive.
        await s1.SaveAsync(Array.Empty<string>());

        var s2 = new LibraryCacheService();
        s2.Load(_movies); s2.Load(_tv);
        Assert.NotNull(s2.TryGet(movie, 1, t));
        Assert.NotNull(s2.TryGet(ep, 2, t));
    }

    [Fact]
    public void FilesOutsideTheLibrary_AreNotCached()
    {
        var s = new LibraryCacheService();
        s.Load(_movies);
        var outside = Path.Combine(Path.GetTempPath(), "Downloads", "x.mkv");
        s.Put(outside, 1, DateTime.UtcNow, E(outside, "x"));
        Assert.Null(s.TryGet(outside, 1, DateTime.UtcNow));
        Assert.Equal(0, s.CachedEntryCount);
    }

    [Fact]
    public void SimilarFolderNames_DontMatchEachOther()
    {
        // "Movies" must not claim files in "Movies 4K".
        var s = new LibraryCacheService();
        s.Load(_movies);
        var sibling = _movies + " 4K";
        var path = Path.Combine(sibling, "x.mkv");
        s.Put(path, 1, DateTime.UtcNow, E(path, "x"));
        Assert.Equal(0, s.CachedEntryCount);
    }

    [Fact]
    public void Evict_And_ClearMemory_CoverEveryFolder()
    {
        var movie = Path.Combine(_movies, "A.mkv");
        var ep    = Path.Combine(_tv, "B.mkv");
        var t     = DateTime.UtcNow;
        var s = new LibraryCacheService();
        s.Load(_movies); s.Load(_tv);
        s.Put(movie, 1, t, E(movie, "A"));
        s.Put(ep, 2, t, E(ep, "B"));

        s.Evict(ep);
        Assert.Null(s.TryGet(ep, 2, t));
        Assert.NotNull(s.TryGet(movie, 1, t));

        s.ClearMemoryCache();
        Assert.False(s.HasCache);
    }
}
