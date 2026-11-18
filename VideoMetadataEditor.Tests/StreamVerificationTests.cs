using System;
using System.IO;
using System.Threading.Tasks;
using VideoMetadataEditor.Services;
using Xunit;
using Kind = VideoMetadataEditor.Services.MediaProbeService.StreamKind;

namespace VideoMetadataEditor.Tests;

/// <summary>
/// Build 173: the stream checker that guards every rewrite, plus real-file tests of
/// Fix Faststart and Remux against a multi-stream sample.
///
/// Assets/multistream.mp4 (12 KB): 1 video, 2 audio (eng, jpn), 1 subtitle (eng, mov_text),
/// cover art, moov atom at the END (so it needs faststart). It's exactly the kind of file
/// the pre-168 Fix Faststart damaged.
/// </summary>
[Collection("OperationJournal")]   // also writes history; don't run alongside the journal tests
public class StreamVerificationTests
{
    private static string Asset(string name) => Path.Combine(AppContext.BaseDirectory, "Assets", name);

    // ── Parsing real tool output ─────────────────────────────────────────────

    [Fact]
    public void ParsesFfprobeJson()
    {
        var r = MediaProbeService.ParseFfprobeJson(File.ReadAllText(Asset("probe_multistream.json")));
        Assert.NotNull(r);
        Assert.Equal(1, r!.Count(Kind.Video));
        Assert.Equal(2, r.Count(Kind.Audio));
        Assert.Equal(1, r.Count(Kind.Subtitle));
        Assert.Equal(1, r.Count(Kind.Cover));
        Assert.Equal(1.0, r.DurationSeconds!.Value, 2);
        Assert.Equal("1 video, 2 audio (eng, jpn), 1 subtitle (eng), cover art", r.Describe());
    }

    [Fact]
    public void ParsesFfmpegInfoOutput_SameAsFfprobe()
    {
        var a = MediaProbeService.ParseFfprobeJson(File.ReadAllText(Asset("probe_multistream.json")))!;
        var b = MediaProbeService.ParseFfmpegInfo(File.ReadAllText(Asset("ffmpeg_info_multistream.txt")))!;
        Assert.NotNull(b);
        Assert.Equal(a.Describe(), b.Describe());
        Assert.Null(MediaProbeService.Compare(a, b));
    }

    // ── Comparison ──────────────────────────────────────────────────────────

    [Fact]
    public void Compare_CatchesTheOldFaststartBug()
    {
        // probe_old_faststart_bug.json = the sample after the pre-168 command
        // ("-c copy -movflags +faststart", no -map 0): jpn audio, subtitle and cover gone.
        var before = MediaProbeService.ParseFfprobeJson(File.ReadAllText(Asset("probe_multistream.json")))!;
        var after  = MediaProbeService.ParseFfprobeJson(File.ReadAllText(Asset("probe_old_faststart_bug.json")))!;

        var problem = MediaProbeService.Compare(before, after);
        Assert.NotNull(problem);
        Assert.Contains("audio tracks: 2 before, 1 after", problem);
        Assert.Contains("subtitle tracks: 1 before, 0 after", problem);
        Assert.Contains("cover art was lost", problem);
        Assert.Contains("jpn", problem);
    }

    [Fact]
    public void Compare_IdenticalIsFine_AndExtraStreamsAreFine()
    {
        var before = MediaProbeService.ParseFfprobeJson(File.ReadAllText(Asset("probe_multistream.json")))!;
        Assert.Null(MediaProbeService.Compare(before, before));

        var more = before with { Streams = new System.Collections.Generic.List<MediaProbeService.StreamInfo>(before.Streams)
            { new(Kind.Audio, "ac3", "fre") } };
        Assert.Null(MediaProbeService.Compare(before, more));
    }

    [Fact]
    public void Compare_CatchesLanguageSwapAndDurationChange()
    {
        var before = new MediaProbeService.ProbeResult(new[]
        {
            new MediaProbeService.StreamInfo(Kind.Video, "h264", "und"),
            new MediaProbeService.StreamInfo(Kind.Audio, "aac", "eng"),
            new MediaProbeService.StreamInfo(Kind.Audio, "aac", "jpn"),
        }, 1500);
        var after = new MediaProbeService.ProbeResult(new[]
        {
            new MediaProbeService.StreamInfo(Kind.Video, "h264", "und"),
            new MediaProbeService.StreamInfo(Kind.Audio, "aac", "eng"),
            new MediaProbeService.StreamInfo(Kind.Audio, "aac", "eng"),   // jpn replaced
        }, 1200);

        var problem = MediaProbeService.Compare(before, after);
        Assert.NotNull(problem);
        Assert.Contains("jpn", problem);
        Assert.Contains("duration changed", problem);
    }

    [Fact]
    public void Compare_IgnoreCover_ForMkvRemux()
    {
        var before = MediaProbeService.ParseFfprobeJson(File.ReadAllText(Asset("probe_multistream.json")))!;
        var noCover = before with { Streams = before.Streams.Where(s => s.Kind != Kind.Cover).ToList() };
        Assert.NotNull(MediaProbeService.Compare(before, noCover));
        Assert.Null(MediaProbeService.Compare(before, noCover, ignoreCover: true));
    }

    // ── Real-file tests (need ffmpeg) ──────────────────────────────────────────
    // CI installs ffmpeg and sets VME_REQUIRE_FFMPEG=1, so these can't silently pass
    // there. On a dev machine without ffmpeg they return early.

    private static bool FfmpegReady()
    {
        if (FfmpegService.Detect(forceRedetect: true) != null) return true;
        if (Environment.GetEnvironmentVariable("VME_REQUIRE_FFMPEG") == "1")
            Assert.Fail("VME_REQUIRE_FFMPEG=1 but ffmpeg wasn't found — install it in the CI workflow.");
        return false;
    }

    private static string CopyFixture(string ext = ".mp4")
    {
        var dir = Path.Combine(Path.GetTempPath(), "vme-stream-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "Show - S01E01 - Pilot" + ext);
        File.Copy(Asset("multistream.mp4"), path);
        return path;
    }

    [Fact]
    public async Task Faststart_KeepsEveryStream_OnRealFile()
    {
        if (!FfmpegReady()) return;
        var path = CopyFixture();
        try
        {
            var before = await MediaProbeService.ProbeAsync(path);
            Assert.NotNull(before);
            Assert.True(MediaHealthService.CheckMp4Faststart(path) == false);   // sample needs the fix

            var res = await FfmpegService.AddFaststartAsync(path);
            Assert.True(res.Success, res.Message);
            Assert.Contains("verified", res.Message);

            var after = await MediaProbeService.ProbeAsync(path);
            Assert.Null(MediaProbeService.Compare(before!, after!));
            Assert.True(MediaHealthService.CheckMp4Faststart(path) == true);
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(path)!,
                "Show - S01E01 - Pilot.faststart.tmp.mp4")));   // no temp left behind
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { } }
    }

    [Fact]
    public async Task RemuxToMkv_KeepsAudioAndSubtitles_OnRealFile()
    {
        if (!FfmpegReady()) return;
        var path = CopyFixture();
        try
        {
            var res = await FfmpegService.RemuxAsync(path, ".mkv");
            Assert.True(res.Success, res.Message);
            Assert.NotNull(res.OutputPath);

            var after = await MediaProbeService.ProbeAsync(res.OutputPath!);
            Assert.NotNull(after);
            Assert.Equal(1, after!.Count(Kind.Video));       // cover NOT turned into a 2nd video track
            Assert.Equal(2, after.Count(Kind.Audio));
            Assert.Equal(1, after.Count(Kind.Subtitle));     // mov_text converted, not dropped
            Assert.True(File.Exists(path));                  // original untouched
        }
        finally { try { Directory.Delete(Path.GetDirectoryName(path)!, true); } catch { } }
    }
}
