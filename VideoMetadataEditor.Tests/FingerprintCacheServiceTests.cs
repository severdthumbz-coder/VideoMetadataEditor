using System;
using System.IO;
using VideoMetadataEditor.Services;
using Xunit;

namespace VideoMetadataEditor.Tests;

/// <summary>
/// Tests for FingerprintCacheService — correctness here matters because a stale
/// cache hit during Deep Scan could group the wrong files as duplicates.
/// The cache keys freshness on file size + last-write time.
/// </summary>
public class FingerprintCacheServiceTests : IDisposable
{
    private readonly string _sandbox;

    public FingerprintCacheServiceTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "vme_fpcache_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, true); }
        catch { }
    }

    private string MakeFile(string name, string content = "audio-data")
    {
        var path = Path.Combine(_sandbox, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Store_ThenTryGet_ReturnsFingerprint()
    {
        var cache = new FingerprintCacheService();
        var file  = MakeFile("a.mp4");

        cache.Store(file, "FINGERPRINT123");

        Assert.Equal("FINGERPRINT123", cache.TryGet(file));
    }

    [Fact]
    public void TryGet_UnknownFile_ReturnsNull()
    {
        var cache = new FingerprintCacheService();
        Assert.Null(cache.TryGet(Path.Combine(_sandbox, "never-stored.mp4")));
    }

    [Fact]
    public void TryGet_AfterFileModified_ReturnsNull_StaleEvicted()
    {
        var cache = new FingerprintCacheService();
        var file  = MakeFile("b.mp4", "original");
        cache.Store(file, "FP_OLD");

        // Modify the file → size + last-write change → cache entry must be stale
        System.Threading.Thread.Sleep(20);
        File.WriteAllText(file, "different content entirely");

        Assert.Null(cache.TryGet(file));   // stale entry evicted, forces re-fingerprint
    }

    [Fact]
    public void Store_NullFingerprint_TryGetReturnsEmpty()
    {
        var cache = new FingerprintCacheService();
        var file  = MakeFile("silent.mp4");

        cache.Store(file, null);   // null = "no audio / decode failed"

        // TryGet normalizes a null stored fingerprint to empty string on read.
        Assert.Equal(string.Empty, cache.TryGet(file));
    }

    [Fact]
    public void IsKnownNoAudio_FileWithFingerprint_ReturnsFalse()
    {
        var cache = new FingerprintCacheService();
        var file  = MakeFile("hasaudio.mp4");
        cache.Store(file, "REALFP");

        Assert.False(cache.IsKnownNoAudio(file));
    }

    [Fact]
    public void IsKnownNoAudio_AfterModification_ReturnsFalse()
    {
        var cache = new FingerprintCacheService();
        var file  = MakeFile("c.mp4", "v1");
        // Empty string is the "no audio" sentinel that IsKnownNoAudio checks for.
        cache.Store(file, string.Empty);
        Assert.True(cache.IsKnownNoAudio(file));

        System.Threading.Thread.Sleep(20);
        File.WriteAllText(file, "v2 bigger content");

        // Modified file is no longer "known no audio" — must be re-checked
        Assert.False(cache.IsKnownNoAudio(file));
    }

    [Fact]
    public void ClearMemory_RemovesAllEntries()
    {
        var cache = new FingerprintCacheService();
        var file  = MakeFile("d.mp4");
        cache.Store(file, "FP");
        Assert.Equal("FP", cache.TryGet(file));

        cache.ClearMemory();

        Assert.Null(cache.TryGet(file));
    }
}
