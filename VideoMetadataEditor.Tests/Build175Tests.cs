using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VideoMetadataEditor.Models;
using VideoMetadataEditor.Services;
using Xunit;

namespace VideoMetadataEditor.Tests;

/// <summary>
/// Build 175: the structured activity log (LogService), audio/subtitle track info for the
/// Library (TrackSummary + cache), and History's partial-undo count.
/// Shares the OperationJournal collection: both write to static log/history state.
/// </summary>
[Collection("OperationJournal")]
public class Build175LogTests : IDisposable
{
    private readonly string _dir;
    private readonly string _originalLogDir;
    private readonly string _originalJournal;

    public Build175LogTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vme-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _originalLogDir = LogService.Directory;
        _originalJournal = OperationJournal.FilePath;
        LogService.Close();
        LogService.Directory = Path.Combine(_dir, "logs");
        OperationJournal.FilePath = Path.Combine(_dir, "history.jsonl");
    }

    public void Dispose()
    {
        LogService.Close();
        LogService.Directory = _originalLogDir;
        OperationJournal.FilePath = _originalJournal;
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static List<LogEntry> Mine(string marker) =>
        LogService.Snapshot().Where(e => e.Message.Contains(marker) || (e.OperationName ?? "").Contains(marker)).ToList();

    // ── Classifying the older free-text lines ────────────────────────────────

    [Theory]
    [InlineData("[01:02:03] ✕ Duplicates error: boom",            LogLevel.Error,   "Duplicates")]
    [InlineData("[01:02:03] ⚠ Watch folder: access denied",       LogLevel.Warning, "Watch")]
    [InlineData("[01:02:03] ✓ Trakt token refreshed.",            LogLevel.Success, "Trakt")]
    [InlineData("[01:02:03] Batch rename: 3 succeeded, 0 failed.", LogLevel.Success, "Rename")]
    [InlineData("[01:02:03] Batch rename: 2 succeeded, 1 failed.", LogLevel.Warning, "Rename")]
    [InlineData("[01:02:03] WRITE FAILED — Movie.mp4",            LogLevel.Error,   "Embed")]
    [InlineData("[01:02:03] Rename failed: name in use",          LogLevel.Error,   "Rename")]
    [InlineData("[01:02:03] ABORTED: not enough space",           LogLevel.Error,   "Copy/Move")]
    [InlineData("[01:02:03] Renamed: a.mp4 → A (2020).mp4",       LogLevel.Info,    "Rename")]
    [InlineData("[01:02:03] 💾 Move/Copy settings saved.",        LogLevel.Info,    "Settings")]
    [InlineData("[01:02:03] Remux suggested but ffmpeg is not available — skipping.", LogLevel.Warning, "FFmpeg")]
    [InlineData("Update check skipped (disabled in Settings → Behaviour).", LogLevel.Info, "Update")]
    public void Classify_WorksOutLevelAndCategory(string raw, LogLevel level, string category)
    {
        var (l, c, _) = LogService.Classify(raw);
        Assert.Equal(level, l);
        Assert.Equal(category, c);
    }

    [Fact]
    public void Classify_StripsTimestamp_AndLeadingMark()
    {
        var (_, _, msg) = LogService.Classify("[12:34:56] ⚠ Watch folder: offline");
        Assert.Equal("Watch folder: offline", msg);
        var (_, _, msg2) = LogService.Classify("[12:34:56] 📁 Watch folder active");
        Assert.Equal("📁 Watch folder active", msg2);   // other emoji are kept
    }

    // ── Writing ──────────────────────────────────────────────────────────────

    [Fact]
    public void Write_GoesToMemory_File_AndListeners()
    {
        LogEntry? heard = null;
        void Listener(LogEntry e) { if (e.Message.Contains("marker-write")) heard = e; }
        LogService.EntryAdded += Listener;
        try
        {
            var e = LogService.Write(LogLevel.Warning, "Rename", "marker-write test", "line one\nline two", @"C:\x\Movie.mp4");
            Assert.Same(e, heard);
            Assert.Contains(LogService.Snapshot(), x => x.Seq == e.Seq);
            Assert.Equal("Movie.mp4", e.FileName);
            Assert.Equal("WARN", e.LevelText);

            // The writer keeps the file open (shared) — read it the same way.
            string text;
            using (var fs = new FileStream(LogService.CurrentFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var sr = new StreamReader(fs)) text = sr.ReadToEnd();
            Assert.Contains("WARN", text);
            Assert.Contains("marker-write test", text);
            Assert.Contains(@"[C:\x\Movie.mp4]", text);
            Assert.Contains("        line two", text);   // detail lines indented
        }
        finally { LogService.EntryAdded -= Listener; }
    }

    [Fact]
    public void BrokenListener_DoesNotBreakTheCaller()
    {
        void Bad(LogEntry _) => throw new InvalidOperationException("listener bug");
        LogService.EntryAdded += Bad;
        try { LogService.Info("App", "marker-broken-listener"); }
        finally { LogService.EntryAdded -= Bad; }
        Assert.Single(Mine("marker-broken-listener"));
    }

    // ── Operations ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Operation_LogsStartAndFinish_AndTagsEverythingInside_IncludingWorkers()
    {
        string id;
        using (var op = LogService.Begin("Library", "marker-op scan"))
        {
            id = op.Id;
            LogService.Info("Library", "marker-op inside");
            await Task.Run(() => LogService.Info("Library", "marker-op worker"));
            op.Outcome = "12 files";
        }
        LogService.Info("Library", "marker-op after");

        var mine = Mine("marker-op");
        var start  = mine.Single(e => e.Message.StartsWith("▶"));
        var finish = mine.Single(e => e.Message.StartsWith("■"));
        Assert.Equal(id, start.OperationId);
        Assert.Equal(id, mine.Single(e => e.Message == "marker-op inside").OperationId);
        Assert.Equal(id, mine.Single(e => e.Message == "marker-op worker").OperationId);
        Assert.Null(mine.Single(e => e.Message == "marker-op after").OperationId);
        Assert.Equal(LogLevel.Success, finish.Level);
        Assert.Contains("finished: 12 files", finish.Message);
        Assert.NotNull(finish.Elapsed);
    }

    [Fact]
    public void Operation_FailAndCancel_SetTheLevel()
    {
        using (var op = LogService.Begin("Copy/Move", "marker-fail copy")) op.Fail(new IOException("disk gone"));
        using (var op = LogService.Begin("Copy/Move", "marker-cancel copy")) op.Cancel();

        var failed = Mine("marker-fail").Single(e => e.Message.StartsWith("■"));
        Assert.Equal(LogLevel.Error, failed.Level);
        Assert.Contains("failed: disk gone", failed.Message);
        Assert.Contains("IOException", failed.Detail);

        var cancelled = Mine("marker-cancel").Single(e => e.Message.StartsWith("■"));
        Assert.Equal(LogLevel.Warning, cancelled.Level);
        Assert.Contains("cancelled", cancelled.Message);
    }

    [Fact]
    public void Operations_Nest_AndRestoreTheParent()
    {
        using var outer = LogService.Begin("Embed", "marker-nest outer");
        using (LogService.Begin("Rename", "marker-nest inner"))
            Assert.NotEqual(outer.Id, LogService.CurrentOperation!.Id);
        Assert.Equal(outer.Id, LogService.CurrentOperation!.Id);
    }

    [Fact]
    public void JournalBatch_IsATimedOperation_WithTheFinalSummary()
    {
        using (var b = OperationJournal.Begin("Rename", "marker-journal batch of 1"))
        {
            OperationJournal.Note("Rename", @"C:\a.mp4", @"C:\A (2020).mp4");
            b.Summary = "marker-journal — 1 renamed";
        }
        var finish = Mine("marker-journal").Single(e => e.Message.StartsWith("■"));
        Assert.Equal("Rename", finish.Category);
        Assert.Contains("1 renamed", finish.Message);
        Assert.Contains("1 file change(s) recorded in History", finish.Message);
        // The note itself is logged at detail level with both paths.
        var note = LogService.Snapshot().Last(e => e.Message.StartsWith("History: Rename") && e.OperationId == finish.OperationId);
        Assert.Equal(LogLevel.Debug, note.Level);
        Assert.Contains(@"C:\a.mp4", note.Detail);
    }

    // ── Files ────────────────────────────────────────────────────────────────

    [Fact]
    public void PruneOldLogs_RemovesOnlyOldVmeLogs()
    {
        Directory.CreateDirectory(LogService.Directory);
        var old    = Path.Combine(LogService.Directory, "VME_2000-01-01.log");
        var recent = Path.Combine(LogService.Directory, $"VME_{DateTime.Now:yyyy-MM-dd}.log");
        var other  = Path.Combine(LogService.Directory, "notes.log");
        foreach (var f in new[] { old, recent, other }) File.WriteAllText(f, "x");

        Assert.Equal(1, LogService.PruneOldLogs());
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(other));
    }

    [Theory]
    [InlineData(250, "250 ms")]
    [InlineData(3200, "3.2 s")]
    [InlineData(125_000, "2m 05s")]
    public void FormatElapsed_IsReadable(int ms, string expected) =>
        Assert.Equal(expected, LogService.FormatElapsed(TimeSpan.FromMilliseconds(ms))
                                         .Replace(',', '.'));   // decimal separator varies by culture

    // ── History: partial undo ────────────────────────────────────────────────

    [Fact]
    public void PartialUndo_CountsWhatsLeft_AndARetryOnlyTouchesThat()
    {
        var a = Path.Combine(_dir, "alpha.mp4");  var aNew = Path.Combine(_dir, "Alpha (2020).mp4");
        var b = Path.Combine(_dir, "beta.mp4");   var bNew = Path.Combine(_dir, "Beta (2021).mp4");
        File.WriteAllText(aNew, "a"); File.WriteAllText(bNew, "b");
        File.WriteAllText(a, "someone else's file");   // a's old name is taken

        using (OperationJournal.Begin("Rename", "two")) { OperationJournal.Note("Rename", a, aNew); OperationJournal.Note("Rename", b, bNew); }
        var rec = OperationJournal.Load().Single(r => r.Kind == "Rename");

        var first = OperationJournal.Undo(rec);
        Assert.Equal(1, first.Restored);                       // beta went back
        var all = OperationJournal.Load();
        var left = OperationJournal.RemainingItems(all.Single(r => r.Id == rec.Id), all);
        Assert.Single(left);
        Assert.Equal(aNew, left[0].To);                        // only alpha is left

        File.Delete(a);                                        // free the name
        var retry = OperationJournal.Undo(all.Single(r => r.Id == rec.Id));
        Assert.Equal(1, retry.Restored);
        Assert.Empty(retry.Skipped);                           // beta isn't retried ("no longer at…")
        Assert.True(OperationJournal.Load().Single(r => r.Id == rec.Id).Undone);
    }
}

/// <summary>Build 175: Library audio/subtitle track info.</summary>
public class Build175TrackTests
{
    private static string Asset(string name) => Path.Combine(AppContext.BaseDirectory, "Assets", name);

    private static string EncodedSample() =>
        TrackSummary.Encode(MediaProbeService.ParseFfprobeJson(File.ReadAllText(Asset("probe_multistream.json")))!);

    [Fact]
    public void Encode_KeepsAudioAndSubtitles_NotVideoOrCover()
    {
        Assert.Equal("a:en:aac|a:ja:aac|s:en:mov_text", EncodedSample());
    }

    [Fact]
    public void FfmpegFallback_EncodesTheSame()
    {
        var viaFfmpeg = TrackSummary.Encode(MediaProbeService.ParseFfmpegInfo(File.ReadAllText(Asset("ffmpeg_info_multistream.txt")))!);
        Assert.Equal(EncodedSample(), viaFfmpeg);
    }

    [Fact]
    public void ForcedFlag_AndBibliographicCodes_AreRead()
    {
        const string json = """
        {"streams":[
          {"codec_type":"video","codec_name":"h264","disposition":{"attached_pic":0}},
          {"codec_type":"audio","codec_name":"ac3","tags":{"language":"fre","title":"Commentary"}},
          {"codec_type":"subtitle","codec_name":"subrip","tags":{"language":"ger"},"disposition":{"forced":1}}
        ],"format":{"duration":"10.0"}}
        """;
        var probe = MediaProbeService.ParseFfprobeJson(json)!;
        Assert.Equal("Commentary", probe.Streams[1].Title);
        Assert.Equal("a:fr:ac3|s:de:subrip:f", TrackSummary.Encode(probe));
        Assert.Equal("DE [F]", TrackSummary.SubtitleDisplay(TrackSummary.Encode(probe)));

        var info = MediaProbeService.ParseFfmpegInfo(
            "Input #0, matroska,webm, from 'x.mkv':\n  Duration: 00:00:10.00, start: 0.000000, bitrate: 1 kb/s\n" +
            "  Stream #0:0(eng): Subtitle: subrip (default) (forced)\n")!;
        Assert.True(info.Streams[0].Forced);
    }

    [Theory]
    [InlineData("eng", "en")] [InlineData("ENG", "en")] [InlineData("fre", "fr")] [InlineData("fra", "fr")]
    [InlineData("chi", "zh")] [InlineData("en-US", "en")] [InlineData("", "und")] [InlineData(null, "und")]
    [InlineData("und", "und")] [InlineData("xyz", "xyz")]
    public void NormalizeLang(string? code, string expected) => Assert.Equal(expected, TrackSummary.NormalizeLang(code));

    [Fact]
    public void Display_NotReadYet_None_One_Several()
    {
        Assert.Equal("", TrackSummary.AudioDisplay(null));
        Assert.Equal("none", TrackSummary.AudioDisplay(""));
        Assert.Equal("none", TrackSummary.SubtitleDisplay("a:en:aac"));
        Assert.Equal("EN", TrackSummary.AudioDisplay("a:en:aac"));
        Assert.Equal("2 · EN, JA", TrackSummary.AudioDisplay(EncodedSample()));
        Assert.Equal("EN", TrackSummary.SubtitleDisplay(EncodedSample()));
        Assert.Equal("?", TrackSummary.AudioDisplay("a:und:aac"));
        Assert.Contains("Audio: Japanese (aac)", TrackSummary.Describe(EncodedSample()));
    }

    [Fact]
    public void Decode_IgnoresJunk()
    {
        var t = TrackSummary.Decode("a:en:aac||x:fr|s|s:es:srt:f|garbage");
        Assert.Equal(2, t.Count);
        Assert.True(t[1].Forced);
    }

    [Fact]
    public void AudioFilter()
    {
        var two = EncodedSample();
        Assert.True(TrackSummary.MatchesAudio("any", null));      // not read yet: only "any"
        Assert.False(TrackSummary.MatchesAudio("lang:en", null));
        Assert.True(TrackSummary.MatchesAudio("lang:ja", two));
        Assert.False(TrackSummary.MatchesAudio("lang:fr", two));
        Assert.True(TrackSummary.MatchesAudio("multi", two));
        Assert.False(TrackSummary.MatchesAudio("multi", "a:en:aac"));
    }

    [Fact]
    public void SubtitleFilter_CountsSidecarsToo()
    {
        var none = "a:en:aac";
        Assert.True(TrackSummary.MatchesSubtitles("none", none, Array.Empty<string>(), 0));
        Assert.False(TrackSummary.MatchesSubtitles("none", none, new[] { "en" }, 1));       // has Movie.en.srt
        Assert.False(TrackSummary.MatchesSubtitles("none", none, Array.Empty<string>(), 1)); // Movie.srt, no language
        Assert.False(TrackSummary.MatchesSubtitles("none", null, Array.Empty<string>(), 0)); // not read yet

        Assert.True(TrackSummary.MatchesSubtitles("lang:en", null, new[] { "eng" }, 1));    // sidecar alone is enough
        Assert.True(TrackSummary.MatchesSubtitles("lang:en", EncodedSample(), Array.Empty<string>(), 0));
        Assert.True(TrackSummary.MatchesSubtitles("missing:es", EncodedSample(), new[] { "fr" }, 1));
        Assert.False(TrackSummary.MatchesSubtitles("missing:en", EncodedSample(), Array.Empty<string>(), 0));
        Assert.False(TrackSummary.MatchesSubtitles("missing:fr", none, new[] { "fr" }, 1));
    }

    [Fact]
    public void FilterOptions_ListLanguagesInTheLibrary_WithCounts()
    {
        var lib = new[] { EncodedSample(), "a:en:aac", "a:fr:ac3|s:fr:srt", null };
        var audio = TrackSummary.BuildAudioOptions(lib);
        Assert.Equal("any", audio[0].Key);
        Assert.Contains(audio, o => o.Key == "multi" && o.Label.Contains("(1)"));
        Assert.Contains(audio, o => o.Key == "lang:en" && o.Label == "English (2)");
        Assert.Equal("lang:en", audio[2].Key);   // most common language first

        var subs = TrackSummary.BuildSubtitleOptions(new (string?, IEnumerable<string>, int)[]
        {
            (EncodedSample(), Array.Empty<string>(), 0),
            ("a:en:aac", new[] { "es" }, 1),
            ("a:en:aac", Array.Empty<string>(), 0),
            (null, Array.Empty<string>(), 0),
        });
        Assert.Contains(subs, o => o.Key == "none" && o.Label.Contains("(1)"));
        Assert.Contains(subs, o => o.Key == "lang:es" && o.Label == "Spanish (1)");
        Assert.Contains(subs, o => o.Key == "missing:en");
    }

    [Fact]
    public void LibraryEntry_ShowsTracks_AndRaisesChanges()
    {
        var e = new LibraryEntry { FilePath = @"C:\m.mkv" };
        var raised = new List<string?>();
        e.PropertyChanged += (_, a) => raised.Add(a.PropertyName);
        Assert.False(e.TracksChecked);
        e.Tracks = EncodedSample();
        Assert.True(e.TracksChecked);
        Assert.Equal("2 · EN, JA", e.AudioTracksDisplay);
        Assert.Contains(nameof(LibraryEntry.AudioTracksDisplay), raised);
        Assert.Contains(nameof(LibraryEntry.EmbeddedSubtitlesDisplay), raised);
    }

    [Fact]
    public async Task Tracks_SurviveTheScanCache_AndOldCachesReadAsNotReadYet()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"VmeTrackCache_{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var file  = Path.Combine(folder, "Movie.mkv");
            var other = Path.Combine(folder, "Other.mkv");
            var mtime = DateTime.UtcNow;

            var s1 = new LibraryCacheService();
            s1.Load(folder);
            s1.Put(file,  100, mtime, new LibraryEntry { FilePath = file,  Title = "Movie" });
            s1.Put(other, 200, mtime, new LibraryEntry { FilePath = other, Title = "Other" });
            Assert.Null(s1.TryGet(file, 100, mtime)!.Tracks);          // not read yet
            Assert.True(s1.UpdateTracks(file, "a:en:aac|s:en:srt"));
            Assert.False(s1.UpdateTracks(Path.Combine(folder, "missing.mkv"), "a:en:aac"));
            await s1.SaveAsync(new[] { file, other });

            var s2 = new LibraryCacheService();
            s2.Load(folder);
            Assert.Equal("a:en:aac|s:en:srt", s2.TryGet(file, 100, mtime)!.Tracks);
            Assert.Null(s2.TryGet(other, 200, mtime)!.Tracks);
            Assert.Equal("Movie", s2.TryGet(file, 100, mtime)!.Title);  // nothing else touched
            s2.Clear(folder);
        }
        finally { try { Directory.Delete(folder, true); } catch { } }
    }
}
