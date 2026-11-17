using System;
using System.Collections.Generic;
using System.IO;
using VideoMetadataEditor.Services;
using Xunit;

namespace VideoMetadataEditor.Tests;

/// <summary>Build 171: poster thumbnail keys and stale-thumbnail cleanup.</summary>
public class ThumbCacheKeysTests
{
    private static readonly DateTime T = new(2026, 11, 17, 5, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Key_ChangesWhenFileIsReembedded()
    {
        var before = ThumbCacheKeys.KeyFor(@"A:\TV\Ep1.mp4", 1000, T);
        var after  = ThumbCacheKeys.KeyFor(@"A:\TV\Ep1.mp4", 1000, T.AddMinutes(5));  // new mtime
        var resized = ThumbCacheKeys.KeyFor(@"A:\TV\Ep1.mp4", 1200, T);               // new size
        Assert.NotEqual(before, after);
        Assert.NotEqual(before, resized);
    }

    [Fact]
    public void Key_IgnoresPathCase()
        => Assert.Equal(ThumbCacheKeys.KeyFor(@"A:\TV\Ep1.mp4", 1, T),
                        ThumbCacheKeys.KeyFor(@"a:\tv\EP1.MP4", 1, T));

    [Fact]
    public void StaleFiles_OnlyThumbsAndMarkersNotInUse()
    {
        var live = ThumbCacheKeys.KeyFor(@"A:\TV\Ep1.mp4", 1, T);
        var dead = ThumbCacheKeys.KeyFor(@"A:\TV\Ep1.mp4", 1, T.AddDays(-1));
        var deadNone = ThumbCacheKeys.KeyFor(@"A:\TV\Gone.mp4", 1, T);
        var folder = Path.Combine("C:", "thumbs");

        var files = new[]
        {
            Path.Combine(folder, live + ".jpg"),
            Path.Combine(folder, dead + ".jpg"),
            Path.Combine(folder, deadNone + ".none"),
            Path.Combine(folder, "readme.txt"),          // not ours — never touched
        };

        var stale = ThumbCacheKeys.StaleFiles(files, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { live });

        Assert.Equal(2, stale.Count);
        Assert.Contains(Path.Combine(folder, dead + ".jpg"), stale);
        Assert.Contains(Path.Combine(folder, deadNone + ".none"), stale);
    }

    [Fact]
    public void KeyForFile_MissingFile_IsNull()
        => Assert.Null(ThumbCacheKeys.KeyForFile(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".mp4")));
}
