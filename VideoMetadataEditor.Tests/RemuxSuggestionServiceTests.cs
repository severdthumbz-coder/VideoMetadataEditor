using VideoMetadataEditor.Services;
using Xunit;
using static VideoMetadataEditor.Services.WriteDiagnosisCategory;

namespace VideoMetadataEditor.Tests;

/// <summary>
/// Build 127 — tests for RemuxSuggestionService.ShouldSuggestRemux: the pure decision
/// of whether a failed write warrants offering a lossless remux. A remux should be
/// suggested only for container/format-level failures (FormatUnsupported, Unknown) on
/// MP4-family files — never for access/permission/volume failures a remux can't fix,
/// and never for non-remuxable extensions.
/// </summary>
public class RemuxSuggestionServiceTests
{
    // ── Suggest for container-level failures on MP4-family files ────────────────

    [Theory]
    [InlineData(FormatUnsupported, "A:\\TV\\Show - S01E01.mp4")]
    [InlineData(Unknown,           "A:\\TV\\Show - S01E01.mp4")]
    [InlineData(FormatUnsupported, "C:\\Movies\\Film.m4v")]
    [InlineData(Unknown,           "C:\\Movies\\Clip.mov")]
    [InlineData(FormatUnsupported, "relative/path/file.MP4")] // case-insensitive ext
    public void Suggests_ForContainerFailure_OnMp4Family(WriteDiagnosisCategory cat, string path)
    {
        Assert.True(RemuxSuggestionService.ShouldSuggestRemux(cat, path));
    }

    // ── Do NOT suggest for access/permission/volume failures ────────────────────

    [Theory]
    [InlineData(ReadOnly)]
    [InlineData(FileLocked)]
    [InlineData(PermissionDenied)]
    [InlineData(NetworkVolume)]
    [InlineData(ReadOnlyVolume)]
    public void DoesNotSuggest_ForNonContainerFailures(WriteDiagnosisCategory cat)
    {
        Assert.False(RemuxSuggestionService.ShouldSuggestRemux(cat, "A:\\TV\\Show.mp4"));
    }

    // ── Do NOT suggest for non-remuxable extensions ─────────────────────────────

    [Theory]
    [InlineData("A:\\TV\\Show.mkv")]   // Matroska → mkvpropedit, not this path
    [InlineData("A:\\TV\\Show.avi")]
    [InlineData("A:\\TV\\Show.webm")]
    [InlineData("A:\\TV\\Show")]        // no extension
    [InlineData("A:\\TV\\Show.")]       // trailing dot, empty ext
    public void DoesNotSuggest_ForNonRemuxableExtensions(string path)
    {
        Assert.False(RemuxSuggestionService.ShouldSuggestRemux(FormatUnsupported, path));
        Assert.False(RemuxSuggestionService.ShouldSuggestRemux(Unknown, path));
    }

    // ── Null / empty path safety ────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DoesNotSuggest_ForNullOrEmptyPath(string? path)
    {
        Assert.False(RemuxSuggestionService.ShouldSuggestRemux(FormatUnsupported, path));
    }

    // ── IsContainerLevelFailure classification ──────────────────────────────────

    [Fact]
    public void IsContainerLevelFailure_OnlyFormatUnsupportedAndUnknown()
    {
        Assert.True(RemuxSuggestionService.IsContainerLevelFailure(FormatUnsupported));
        Assert.True(RemuxSuggestionService.IsContainerLevelFailure(Unknown));
        Assert.False(RemuxSuggestionService.IsContainerLevelFailure(ReadOnly));
        Assert.False(RemuxSuggestionService.IsContainerLevelFailure(FileLocked));
        Assert.False(RemuxSuggestionService.IsContainerLevelFailure(PermissionDenied));
        Assert.False(RemuxSuggestionService.IsContainerLevelFailure(NetworkVolume));
        Assert.False(RemuxSuggestionService.IsContainerLevelFailure(ReadOnlyVolume));
    }
}
