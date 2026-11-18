using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VideoMetadataEditor.Models;
using VideoMetadataEditor.Services;
using Xunit;
using Kind = VideoMetadataEditor.Services.MediaProbeService.StreamKind;

namespace VideoMetadataEditor.Tests;

/// <summary>
/// Build 177: a title with a trailing space ("One Last Shot ") made the post-write check
/// fail on a write that had worked, which led to retries, an in-place write and a remux.
/// Whitespace is now cleaned before writing.
/// </summary>
public class MetadataTextTests
{
    [Theory]
    [InlineData("One Last Shot ", "One Last Shot")]
    [InlineData("  One   Last\tShot ", "One Last Shot")]
    [InlineData("One​Last Shot", "OneLast Shot")]     // zero-width space removed
    [InlineData("Line\nbreak", "Line break")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void CleanLine(string? input, string expected) => Assert.Equal(expected, MetadataText.CleanLine(input));

    [Fact]
    public void CleanBlock_KeepsParagraphs_DropsExtraBlankLines()
    {
        var s = "\n\n  First  line. \r\n\r\n\r\n Second paragraph.  \n\n";
        Assert.Equal($"First line.{Environment.NewLine}{Environment.NewLine}Second paragraph.", MetadataText.CleanBlock(s));
    }

    [Fact]
    public void Normalize_FixesEveryField_AndReportsWhichChanged()
    {
        var m = new MovieMetadata
        {
            Title = "One Last Shot ", Year = "2026", Genre = "Action,  Thriller",
            Director = " Jane Doe", Description = "Plot.\n\n\n\nMore.", ImdbId = "tt44898377",
            IsEpisode = true, ShowTitle = "Hyper  Knife", EpisodeTitle = "Pilot ",
        };
        var changed = MetadataText.Normalize(m);

        Assert.Equal("One Last Shot", m.Title);
        Assert.Equal("Action, Thriller", m.Genre);
        Assert.Equal("Jane Doe", m.Director);
        Assert.Equal("Hyper Knife", m.ShowTitle);
        Assert.Equal("Pilot", m.EpisodeTitle);
        Assert.Contains(nameof(MovieMetadata.Title), changed);
        Assert.Contains(nameof(MovieMetadata.Description), changed);
        Assert.DoesNotContain(nameof(MovieMetadata.Year), changed);
        Assert.DoesNotContain(nameof(MovieMetadata.ImdbId), changed);

        Assert.Empty(MetadataText.Normalize(m));   // already clean → nothing changes
    }

    [Fact]
    public void SameText_IgnoresWhitespaceAndCase_ButNotRealDifferences()
    {
        Assert.True(MetadataText.SameText("One Last Shot ", "One Last Shot"));
        Assert.True(MetadataText.SameText("one last shot", "One Last Shot"));
        Assert.False(MetadataText.SameText("One Last Shot", "One Last Shoot"));
    }

    [Theory]
    [InlineData("[07:05:47] Tag write failed: Post-write verification failed — Title was not committed. — falling back to in-place write",
                LogLevel.Warning, "Embed")]
    [InlineData("[07:05:50] ERROR: all write methods failed for x.mp4", LogLevel.Error, "Embed")]
    [InlineData("[07:05:48] Retry 2/3: One.Last.Shot.mp4", LogLevel.Info, "Embed")]
    [InlineData("[07:05:47] In-place write: One.Last.Shot.mp4", LogLevel.Info, "Embed")]
    public void Classify_FallbackIsAWarning_FinalFailureIsAnError(string raw, LogLevel level, string category)
    {
        var (l, c, _) = LogService.Classify(raw);
        Assert.Equal(level, l);
        Assert.Equal(category, c);
    }
}

public class TrailingSpaceWriteTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "vme-ws-" + Guid.NewGuid().ToString("N"));
    public TrailingSpaceWriteTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public async Task TitleWithTrailingSpace_WritesFirstTime_AndIsStoredClean()
    {
        var path = Path.Combine(_dir, "One.Last.Shot.2026.mp4");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "tiny.mp4"), path);
        var svc  = new MetadataService();
        var meta = new MovieMetadata { Title = "One Last Shot ", Year = "2026" };

        var result = await svc.WriteMetadataDetailedAsync(path, meta, new AppSettings
        {
            AutoRenameEnabled = false, VerboseWriteErrors = false, ArtworkJpegQuality = 85, ArtworkMaxPx = 1000,
        });

        Assert.True(result.Success, result.Diagnosis?.Reasons.FirstOrDefault() ?? "write failed");
        Assert.Equal("One Last Shot", svc.ReadMetadataFast(path).Title);
        Assert.Equal("One Last Shot", meta.Title);   // the caller's copy is cleaned too (used for the rename)
    }
}

/// <summary>Build 177: an MP4 → MP4 remux keeps every stream, cover art included.</summary>
[Collection("OperationJournal")]   // writes history
public class Mp4RemuxKeepsEverythingTests
{
    [Fact]
    public async Task RemuxMp4ToMp4_KeepsCoverAndAllTracks_OnRealFile()
    {
        if (FfmpegService.Detect(forceRedetect: true) == null)
        {
            if (Environment.GetEnvironmentVariable("VME_REQUIRE_FFMPEG") == "1")
                Assert.Fail("VME_REQUIRE_FFMPEG=1 but ffmpeg wasn't found.");
            return;
        }
        var dir = Path.Combine(Path.GetTempPath(), "vme-remux-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "Movie (2026).mp4");
            File.Copy(Path.Combine(AppContext.BaseDirectory, "Assets", "multistream.mp4"), path);
            var before = await MediaProbeService.ProbeAsync(path);

            var res = await FfmpegService.RemuxAsync(path, ".mp4");
            Assert.True(res.Success, res.Message);
            Assert.Contains("verified", res.Message);   // no "not everything carried over"

            var after = await MediaProbeService.ProbeAsync(res.OutputPath!);
            Assert.Null(MediaProbeService.Compare(before!, after!));
            Assert.Equal(1, after!.Count(Kind.Cover));
            Assert.Equal(2, after.Count(Kind.Audio));
            Assert.Equal(1, after.Count(Kind.Subtitle));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
