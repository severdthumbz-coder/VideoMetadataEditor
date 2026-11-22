using VideoMetadataEditor.Services;
using Xunit;

namespace VideoMetadataEditor.Tests;

/// <summary>
/// Build 180: film titles in log lines no longer decide the level. Library Watch re-adding
/// the library logged "12th Fail (2023).mp4 added" as an ERROR, "Missing (2023).mp4" as a
/// WARNING and "A Complete Unknown (2024).mp4" as a success.
/// </summary>
public class LogTitleClassificationTests
{
    [Theory]
    [InlineData("[11:39:30] 📚 Library watch: 12th Fail (2023).mp4 added.",           LogLevel.Info,    "Library")]
    [InlineData("[11:49:56] 📚 Library watch: Missing (2023).mp4 added.",             LogLevel.Info,    "Library")]
    [InlineData("[11:39:20] 📚 Library watch: A Complete Unknown (2024).mp4 added.",  LogLevel.Info,    "Library")]
    [InlineData("[07:05:09] Writing tags: 12th Fail (2023).mp4",                      LogLevel.Info,    "Embed")]
    [InlineData("[07:05:09] Renamed: 12th.Fail.2023.mp4 → 12th Fail (2023).mp4",      LogLevel.Info,    "Rename")]
    [InlineData("[07:05:13] Done: The Copy Error (2020).mp4",                         LogLevel.Success, "Embed")]
    [InlineData("[07:05:50] WRITE FAILED — 12th Fail (2023).mp4: TagLib# cannot write", LogLevel.Error, "Embed")]
    [InlineData("[07:05:50] ⚠ Auto-embed: rejected 'Missing' (2023) for query 'Missing'", LogLevel.Warning, "Embed")]
    public void TitlesDontDecideTheLevel(string raw, LogLevel level, string category)
    {
        var (l, c, _) = LogService.Classify(raw);
        Assert.Equal(level, l);
        Assert.Equal(category, c);
    }

    [Fact]
    public void Scrub_RemovesFileNamesAndQuotedTitles_KeepsTheWordsAround()
    {
        Assert.Equal("WRITE FAILED —  : TagLib# cannot write",
            LogService.Scrub("WRITE FAILED — One.Last.Shot.2026.mp4: TagLib# cannot write"));
        Assert.DoesNotContain("Fail", LogService.Scrub("Library watch: 12th Fail (2023).mp4 added"));
        Assert.DoesNotContain("Missing", LogService.Scrub("Auto-embed skipped: 'Missing' already has metadata"));
    }
}
