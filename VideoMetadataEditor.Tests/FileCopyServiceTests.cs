using System;
using System.IO;
using VideoMetadataEditor.Services;
using Xunit;

namespace VideoMetadataEditor.Tests;

/// <summary>
/// Tests for FileCopyService.ResolveDestination — the routing logic that decides
/// whether a file lands in Movies, TV Shows/Show/Season, or Unsorted. This was the
/// source of the "untagged S01E02 file dumped in Movies" bug, so the filename-rescue
/// path is covered explicitly.
///
/// Note: ResolveDestination calls Directory.CreateDirectory, so tests run against a
/// temp sandbox base and clean up afterward.
/// </summary>
public class FileCopyServiceTests : IDisposable
{
    private readonly string _base;

    public FileCopyServiceTests()
    {
        _base = Path.Combine(Path.GetTempPath(), "vme_resolve_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_base);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_base)) Directory.Delete(_base, true); }
        catch { }
    }

    [Fact]
    public void SmartOrganiseOff_ReturnsBaseUnchanged()
    {
        var result = FileCopyService.ResolveDestination(
            _base, isEpisode: false, showTitle: "", showYear: "", season: null,
            smartOrganise: false);
        Assert.Equal(_base, result);
    }

    [Fact]
    public void TaggedMovie_RoutesToMoviesFolder()
    {
        var result = FileCopyService.ResolveDestination(
            _base, isEpisode: false, showTitle: "", showYear: "", season: null,
            smartOrganise: true, title: "Inception");
        Assert.Equal(Path.Combine(_base, "Movies"), result);
    }

    [Fact]
    public void TaggedEpisode_RoutesToTvShowSeasonFolder()
    {
        var result = FileCopyService.ResolveDestination(
            _base, isEpisode: true, showTitle: "Breaking Bad", showYear: "2008", season: 2,
            smartOrganise: true, createSeasonFolders: true);
        Assert.Equal(
            Path.Combine(_base, "TV Shows", "Breaking Bad", "Season 02"),
            result);
    }

    [Fact]
    public void Episode_NoSeasonFolders_RoutesToShowFolderOnly()
    {
        var result = FileCopyService.ResolveDestination(
            _base, isEpisode: true, showTitle: "Firefly", showYear: "", season: 1,
            smartOrganise: true, createSeasonFolders: false);
        Assert.Equal(Path.Combine(_base, "TV Shows", "Firefly"), result);
    }

    // ── The bug this regression-guards: untagged S##E## must NOT go to Movies ──

    [Fact]
    public void UntaggedFile_WithEpisodeFilename_RescuedToTvFolder()
    {
        // No embedded metadata, but filename says S02E04 → must route to TV Shows
        var result = FileCopyService.ResolveDestination(
            _base, isEpisode: false, showTitle: "", showYear: "", season: null,
            smartOrganise: true, title: "",
            filePath: @"C:\incoming\Bloodhounds.S02E04.1080p.mkv");

        Assert.Contains(Path.Combine("TV Shows"), result);
        Assert.DoesNotContain("Movies", result);
        Assert.DoesNotContain("Unsorted", result);
    }

    [Fact]
    public void TrulyUntagged_SendToUnsorted()
    {
        var result = FileCopyService.ResolveDestination(
            _base, isEpisode: false, showTitle: "", showYear: "", season: null,
            smartOrganise: true, title: "",
            untaggedHandling: "SendToUnsorted", unsortedFolderName: "Unsorted",
            filePath: @"C:\incoming\random_clip_8842.mp4");

        Assert.Equal(Path.Combine(_base, "Unsorted"), result);
    }

    [Fact]
    public void TrulyUntagged_SkipReturnsEmptySentinel()
    {
        var result = FileCopyService.ResolveDestination(
            _base, isEpisode: false, showTitle: "", showYear: "", season: null,
            smartOrganise: true, title: "",
            untaggedHandling: "Skip",
            filePath: @"C:\incoming\random_clip_8842.mp4");

        Assert.Equal(string.Empty, result);   // caller treats empty as "skip this file"
    }

    [Fact]
    public void TrulyUntagged_SendToMovies_FallsThroughToMovies()
    {
        var result = FileCopyService.ResolveDestination(
            _base, isEpisode: false, showTitle: "", showYear: "", season: null,
            smartOrganise: true, title: "",
            untaggedHandling: "SendToMovies",
            filePath: @"C:\incoming\random_clip_8842.mp4");

        Assert.Equal(Path.Combine(_base, "Movies"), result);
    }

    [Fact]
    public void CustomFolderNames_Respected()
    {
        var result = FileCopyService.ResolveDestination(
            _base, isEpisode: true, showTitle: "Dark", showYear: "", season: 1,
            smartOrganise: true,
            tvFolderName: "Series", createSeasonFolders: true);
        Assert.Equal(Path.Combine(_base, "Series", "Dark", "Season 01"), result);
    }
}
