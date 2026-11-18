using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using VideoMetadataEditor.Services;
using Xunit;

namespace VideoMetadataEditor.Tests;

/// <summary>Build 174: persistent operation history and Undo.</summary>
[Collection("OperationJournal")]
public class OperationJournalTests : IDisposable
{
    private readonly string _dir;
    private readonly string _originalPath;

    public OperationJournalTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "vme-journal-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _originalPath = OperationJournal.FilePath;
        OperationJournal.FilePath = Path.Combine(_dir, "history.jsonl");
    }

    public void Dispose()
    {
        OperationJournal.FilePath = _originalPath;
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Touch(string relative, string content = "x")
    {
        var p = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, content);
        return p;
    }

    [Fact]
    public void Batch_GroupsNotes_IntoOneEntry()
    {
        using (var b = OperationJournal.Begin("Rename", "Batch rename of 2"))
        {
            OperationJournal.Note("Rename", @"C:\a.mp4", @"C:\A (2020).mp4");
            OperationJournal.Note("Rename", @"C:\b.mp4", @"C:\B (2021).mp4");
            b.Summary = "Batch rename — 2 renamed";
        }
        var all = OperationJournal.Load();
        Assert.Single(all);
        Assert.Equal("Batch rename — 2 renamed", all[0].Summary);
        Assert.Equal(2, all[0].Items.Count);
        Assert.True(all[0].CanUndo);
    }

    [Fact]
    public void EmptyBatch_IsNotRecorded()
    {
        using (OperationJournal.Begin("Rename", "nothing happened")) { }
        Assert.Empty(OperationJournal.Load());
    }

    [Fact]
    public void NoteWithoutBatch_IsItsOwnEntry_AndNewestComesFirst()
    {
        OperationJournal.Note("Rename", @"C:\one.mp4", @"C:\One.mp4");
        OperationJournal.Note("Faststart", @"C:\two.mp4", @"C:\two.mp4");
        var all = OperationJournal.Load();
        Assert.Equal(2, all.Count);
        Assert.Equal("Faststart", all[0].Kind);           // newest first
        Assert.False(all[0].CanUndo);                     // in-place rewrite: record only
        Assert.True(all[1].CanUndo);
    }

    [Fact]
    public async Task Batch_FlowsIntoParallelWorkers()
    {
        using (OperationJournal.Begin("Move", "parallel"))
        {
            await Task.WhenAll(Enumerable.Range(0, 20).Select(i => Task.Run(() =>
                OperationJournal.Note("Move", $@"C:\src\{i}.mp4", $@"D:\dst\{i}.mp4"))));
        }
        var all = OperationJournal.Load();
        Assert.Single(all);
        Assert.Equal(20, all[0].Items.Count);
    }

    [Fact]
    public void Undo_PutsFilesBack_WithSidecars_AndRecordsIt()
    {
        var from = Touch(@"Show\Season 01\show.s01e01.mp4", "video");
        var to   = Path.Combine(_dir, @"Show\Season 01\Show - S01E01 - Pilot.mp4");
        File.Move(from, to);
        Touch(@"Show\Season 01\Show - S01E01 - Pilot.en.srt", "subs");
        Touch(@"Show\Season 01\Show - S01E01 - Pilot.nfo", "nfo");
        Touch(@"Show\Season 01\Show - S01E01 - Pilot.mkv", "a different video — must NOT move");

        using (OperationJournal.Begin("Rename", "rename")) OperationJournal.Note("Rename", from, to);
        var rec = OperationJournal.Load().Single();

        var outcome = OperationJournal.Undo(rec);

        Assert.Equal(1, outcome.Restored);
        Assert.Empty(outcome.Skipped);
        Assert.True(File.Exists(from));
        Assert.False(File.Exists(to));
        Assert.True(File.Exists(Path.Combine(_dir, @"Show\Season 01\show.s01e01.en.srt")));
        Assert.True(File.Exists(Path.Combine(_dir, @"Show\Season 01\show.s01e01.nfo")));
        Assert.True(File.Exists(Path.Combine(_dir, @"Show\Season 01\Show - S01E01 - Pilot.mkv")));

        var after = OperationJournal.Load();
        Assert.Equal(2, after.Count);
        Assert.Equal("Undo", after[0].Kind);
        Assert.True(after.Single(r => r.Id == rec.Id).Undone);
        Assert.False(after.Single(r => r.Id == rec.Id).CanUndo);
    }

    [Fact]
    public void Undo_NeverOverwrites_AndSkipsMissingFiles()
    {
        var a = Touch("a.mp4", "original-a");
        var aNew = Path.Combine(_dir, "A.mp4");
        File.Move(a, aNew);
        File.WriteAllText(a, "someone else's new file");   // old name now taken

        var b = Path.Combine(_dir, "b.mp4");
        var bNew = Path.Combine(_dir, "B.mp4");               // never existed / moved away since

        using (OperationJournal.Begin("Rename", "two")) { OperationJournal.Note("Rename", a, aNew); OperationJournal.Note("Rename", b, bNew); }
        var outcome = OperationJournal.Undo(OperationJournal.Load().Single());

        Assert.Equal(0, outcome.Restored);
        Assert.Equal(2, outcome.Skipped.Count);
        Assert.Equal("someone else's new file", File.ReadAllText(a));   // untouched
        Assert.True(File.Exists(aNew));
    }

    [Fact]
    public void Undo_Move_RecreatesTheOriginalFolder()
    {
        var src = Path.Combine(_dir, @"Incoming\Movie (2020).mp4");
        var dst = Touch(@"Library\Movies\Movie (2020)\Movie (2020).mp4", "movie");
        using (OperationJournal.Begin("Move", "move")) OperationJournal.Note("Move", src, dst);

        var outcome = OperationJournal.Undo(OperationJournal.Load().Single());

        Assert.Equal(1, outcome.Restored);
        Assert.True(File.Exists(src));
        Assert.False(File.Exists(dst));
    }

    [Fact]
    public void CopiesAndRewrites_AreRecordOnly()
    {
        using (OperationJournal.Begin("Copy", "copy")) OperationJournal.Note("Copy", @"C:\x.mp4", @"D:\x.mp4");
        Assert.False(OperationJournal.Load().Single().CanUndo);
    }

    [Fact]
    public void DamagedLine_IsSkipped_NotFatal()
    {
        OperationJournal.Note("Rename", @"C:\1.mp4", @"C:\One.mp4");
        File.AppendAllText(OperationJournal.FilePath, "{ this is not json\n");
        OperationJournal.Note("Rename", @"C:\2.mp4", @"C:\Two.mp4");
        Assert.Equal(2, OperationJournal.Load().Count);
    }
}
