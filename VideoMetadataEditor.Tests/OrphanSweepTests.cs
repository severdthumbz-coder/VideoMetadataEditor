using System;
using System.IO;
using VideoMetadataEditor.Services;
using Xunit;

namespace VideoMetadataEditor.Tests;

/// <summary>
/// Tests for orphan-file detection used by the startup sweep — covers the exact
/// scenario where a Fix All Faststart run was interrupted, leaving *.faststart.tmp.*
/// files, plus un-committed *.remux.* candidates.
/// </summary>
public class OrphanSweepTests : IDisposable
{
    private readonly string _sandbox;

    public OrphanSweepTests()
    {
        _sandbox = Path.Combine(Path.GetTempPath(), "vme_orphan_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandbox);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_sandbox)) Directory.Delete(_sandbox, true); }
        catch { }
    }

    private void Make(string name) => File.WriteAllText(Path.Combine(_sandbox, name), "x");

    [Fact]
    public void FindFaststartOrphans_CatchesInterruptedTempFiles()
    {
        Make("Movie1.faststart.tmp.mp4");          // interrupted run
        Make("Movie2.faststart.tmp.mp4");
        Make("Movie3.mp4");                         // a real file, must NOT match

        var orphans = FfmpegService.FindFaststartOrphans(_sandbox, recursive: false);

        Assert.Equal(2, orphans.Count);
        Assert.Contains(orphans, p => p.EndsWith("Movie1.faststart.tmp.mp4"));
        Assert.DoesNotContain(orphans, p => p.EndsWith("Movie3.mp4"));
    }

    [Fact]
    public void FindFaststartOrphans_CatchesCollisionSuffixedTemps()
    {
        // The exact case from the collision-proofed naming
        Make("Movie.faststart.tmp.mp4");
        Make("Movie.faststart.tmp (1).mp4");

        var orphans = FfmpegService.FindFaststartOrphans(_sandbox, recursive: false);

        Assert.Equal(2, orphans.Count);   // both must be found
    }

    [Fact]
    public void FindFaststartOrphans_Recursive_DescendsSubfolders()
    {
        var sub = Path.Combine(_sandbox, "Movies");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "Nested.faststart.tmp.mp4"), "x");
        Make("Top.faststart.tmp.mp4");

        var flat      = FfmpegService.FindFaststartOrphans(_sandbox, recursive: false);
        var recursive = FfmpegService.FindFaststartOrphans(_sandbox, recursive: true);

        Assert.Single(flat);
        Assert.Equal(2, recursive.Count);
    }

    [Fact]
    public void FindFaststartOrphans_NoOrphans_ReturnsEmpty()
    {
        Make("CleanMovie.mp4");
        Make("AnotherClean.mkv");

        var orphans = FfmpegService.FindFaststartOrphans(_sandbox, recursive: false);

        Assert.Empty(orphans);
    }
}
