using System.IO;
using VideoMetadataEditor.Services;
using Xunit;
using static VideoMetadataEditor.Services.ArtworkSidecarService;

namespace VideoMetadataEditor.Tests;

/// <summary>
/// Build 124 — tests for ArtworkSidecarService: the pure sidecar-path naming logic
/// (Kodi vs Plex/Jellyfin, movie vs episode) and the write wrapper's no-art / overwrite
/// behaviour. Uses a temp directory for the write tests.
/// </summary>
public class ArtworkSidecarServiceTests
{
    // ── Naming: Kodi style ───────────────────────────────────────────────────────

    [Fact]
    public void Kodi_Movie_UsesPosterSuffix()
    {
        var p = SidecarPathFor(Path.Combine("X", "The Matrix (1999).mkv"), ArtworkNaming.Kodi, isEpisode: false);
        Assert.Equal(Path.Combine("X", "The Matrix (1999)-poster.jpg"), p);
    }

    [Fact]
    public void Kodi_Episode_UsesThumbSuffix()
    {
        var p = SidecarPathFor(Path.Combine("X", "Show - S01E01.mkv"), ArtworkNaming.Kodi, isEpisode: true);
        Assert.Equal(Path.Combine("X", "Show - S01E01-thumb.jpg"), p);
    }

    // ── Naming: Plex/Jellyfin folder style ─────────────────────────────────────────

    [Fact]
    public void PlexJellyfin_Movie_UsesFolderPoster()
    {
        var p = SidecarPathFor(Path.Combine("Movies", "The Matrix (1999)", "The Matrix (1999).mkv"),
            ArtworkNaming.PlexJellyfin, isEpisode: false);
        Assert.Equal(Path.Combine("Movies", "The Matrix (1999)", "poster.jpg"), p);
    }

    [Fact]
    public void PlexJellyfin_Episode_AlsoUsesFolderPoster()
    {
        var p = SidecarPathFor(Path.Combine("TV", "Show", "Show - S01E01.mkv"),
            ArtworkNaming.PlexJellyfin, isEpisode: true);
        Assert.Equal(Path.Combine("TV", "Show", "poster.jpg"), p);
    }

    [Fact]
    public void ShowFolderPoster_IsFolderPosterJpg()
    {
        var p = ShowFolderPosterPathFor(Path.Combine("TV", "Show", "Show - S01E01.mkv"));
        Assert.Equal(Path.Combine("TV", "Show", "poster.jpg"), p);
    }

    // ── Write wrapper ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Write_NoArt_ReturnsFalse_DoesNotThrow()
    {
        var (ok, _, err) = await WriteSidecarAsync("whatever.mkv", null, ArtworkNaming.Kodi, false);
        Assert.False(ok);
        Assert.NotNull(err);
    }

    [Fact]
    public async Task Write_EmptyArt_ReturnsFalse()
    {
        var (ok, _, _) = await WriteSidecarAsync("whatever.mkv", System.Array.Empty<byte>(), ArtworkNaming.Kodi, false);
        Assert.False(ok);
    }

    [Fact]
    public async Task Write_WritesBytesToExpectedPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vme_art_" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var video = Path.Combine(dir, "Movie (2020).mp4");
            var bytes = new byte[] { 1, 2, 3, 4 };
            var (ok, path, err) = await WriteSidecarAsync(video, bytes, ArtworkNaming.Kodi, false);
            Assert.True(ok, err);
            Assert.Equal(Path.Combine(dir, "Movie (2020)-poster.jpg"), path);
            Assert.True(File.Exists(path));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Write_NoOverwrite_LeavesExistingUntouched()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vme_art_" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var video = Path.Combine(dir, "Movie (2020).mp4");
            var path = Path.Combine(dir, "Movie (2020)-poster.jpg");
            await File.WriteAllBytesAsync(path, new byte[] { 9, 9 });

            var (ok, _, _) = await WriteSidecarAsync(video, new byte[] { 1, 2, 3 },
                ArtworkNaming.Kodi, false, overwrite: false);
            Assert.True(ok);
            Assert.Equal(new byte[] { 9, 9 }, await File.ReadAllBytesAsync(path)); // unchanged
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task Write_Overwrite_ReplacesExisting()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vme_art_" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        try
        {
            var video = Path.Combine(dir, "Movie (2020).mp4");
            var path = Path.Combine(dir, "Movie (2020)-poster.jpg");
            await File.WriteAllBytesAsync(path, new byte[] { 9, 9 });

            var (ok, _, _) = await WriteSidecarAsync(video, new byte[] { 1, 2, 3 },
                ArtworkNaming.Kodi, false, overwrite: true);
            Assert.True(ok);
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
        }
        finally { Directory.Delete(dir, true); }
    }
}
