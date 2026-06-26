using System.IO;
using VideoMetadataEditor.Models;

namespace VideoMetadataEditor.Services;

/// <summary>
/// Writes poster artwork as sidecar image files next to the media, in the naming
/// convention Plex, Jellyfin, Emby, and Kodi read from disk.
///
/// Two naming styles (selectable):
///   • Kodi     — &lt;video basename&gt;-poster.jpg          (works everywhere; default)
///   • PlexJellyfin — poster.jpg in the media's folder      (folder-based)
/// For TV episodes the per-episode thumb is &lt;video basename&gt;-thumb.jpg (Kodi) or, in
/// folder style, the show-folder poster.jpg is written instead (series art).
///
/// <see cref="SidecarPathFor"/> is pure (no disk) and unit-tested. WriteSidecarAsync
/// is the thin file-writing wrapper.
///
/// Scope (Build 124): poster only. Fanart/backdrop is intentionally deferred — the
/// metadata model carries no backdrop URL yet, so adding it cleanly is a later build.
/// </summary>
public static class ArtworkSidecarService
{
    public enum ArtworkNaming { Kodi, PlexJellyfin }

    /// <summary>
    /// Returns the sidecar image path for a video file under the chosen naming style.
    /// Pure — does not touch disk.
    ///   Kodi movie/episode  →  &lt;dir&gt;/&lt;basename&gt;-poster.jpg  (episode: -thumb.jpg)
    ///   PlexJellyfin        →  &lt;dir&gt;/poster.jpg              (folder art)
    /// </summary>
    public static string SidecarPathFor(string videoPath, ArtworkNaming naming, bool isEpisode)
    {
        var dir  = Path.GetDirectoryName(videoPath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(videoPath);

        return naming switch
        {
            ArtworkNaming.Kodi => Path.Combine(dir, $"{name}{(isEpisode ? "-thumb" : "-poster")}.jpg"),
            ArtworkNaming.PlexJellyfin => Path.Combine(dir, "poster.jpg"),
            _ => Path.Combine(dir, $"{name}-poster.jpg"),
        };
    }

    /// <summary>
    /// Returns the show-folder poster path (poster.jpg in the episode's folder).
    /// Used for series-level art alongside tvshow.nfo.
    /// </summary>
    public static string ShowFolderPosterPathFor(string episodeVideoPath)
        => Path.Combine(Path.GetDirectoryName(episodeVideoPath) ?? string.Empty, "poster.jpg");

    /// <summary>
    /// Writes <paramref name="artworkBytes"/> to the sidecar path for the given video.
    /// Returns (ok, path, error). Never throws. Does nothing (returns ok=false with a
    /// reason) when there are no bytes to write.
    /// </summary>
    public static async Task<(bool ok, string path, string? error)> WriteSidecarAsync(
        string videoPath, byte[]? artworkBytes, ArtworkNaming naming, bool isEpisode,
        bool overwrite = true, CancellationToken ct = default)
    {
        if (artworkBytes is not { Length: > 0 })
            return (false, videoPath, "No artwork to write.");
        try
        {
            var path = SidecarPathFor(videoPath, naming, isEpisode);
            if (!overwrite && File.Exists(path))
                return (true, path, null); // treat existing as success, leave as-is
            await File.WriteAllBytesAsync(path, artworkBytes, ct);
            return (true, path, null);
        }
        catch (OperationCanceledException) { return (false, videoPath, "Cancelled."); }
        catch (Exception ex) { return (false, videoPath, ex.Message); }
    }
}
