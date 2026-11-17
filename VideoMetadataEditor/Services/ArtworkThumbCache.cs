using System.IO;
using System.Windows.Media.Imaging;

namespace VideoMetadataEditor.Services;

/// <summary>
/// Persistent cache of small poster thumbnails for the Library grid and TV tree (build 170).
///
/// Why: the library cache stores every field EXCEPT artwork, so each launch re-opened
/// every video (thousands, often on a USB drive) just to pull out its cover. TV posters
/// waited behind the whole movie library.
///
/// Now each cover is shrunk once to a ~160 px-tall JPEG and saved in "thumbs\" next to
/// the EXE (portable, like config.json). Later launches read those few-KB files from the
/// local disk instead of opening the videos. Files with no artwork get a tiny ".none"
/// marker so they aren't re-opened every launch either.
///
/// Key = file path + size + last-write time, so re-embedding new artwork (which changes
/// the file) naturally produces a new thumbnail. Thumbnails are display-only: NFO export,
/// embedding and the Files panel always read full artwork from the video itself.
/// </summary>
public static class ArtworkThumbCache
{
    public const int ThumbHeight = 160;   // tree shows 52 px; 3× covers high-DPI screens

    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "thumbs");

    private static string KeyFor(string path, long size, DateTime lastWriteUtc)
        => ThumbCacheKeys.KeyFor(path, size, lastWriteUtc);

    /// <summary>
    /// Looks up a thumbnail for this exact file version.
    /// Returns true when the answer is known: <paramref name="thumb"/> is the JPEG, or
    /// null when the file is known to have no artwork. Returns false on a cache miss.
    /// </summary>
    public static bool TryGet(string videoPath, out byte[]? thumb)
    {
        thumb = null;
        try
        {
            var fi = new FileInfo(videoPath);
            if (!fi.Exists) return false;
            var key = KeyFor(videoPath, fi.Length, fi.LastWriteTimeUtc);

            var jpg = Path.Combine(Folder, key + ".jpg");
            if (File.Exists(jpg)) { thumb = File.ReadAllBytes(jpg); return thumb.Length > 0; }
            return File.Exists(Path.Combine(Folder, key + ".none"));
        }
        catch { return false; }
    }

    /// <summary>
    /// Stores the thumbnail for <paramref name="videoPath"/> made from its full artwork
    /// (or a "no artwork" marker when <paramref name="fullArt"/> is empty) and returns the
    /// thumbnail bytes to display. Falls back to the full artwork if shrinking fails.
    /// </summary>
    public static byte[]? Store(string videoPath, byte[]? fullArt)
    {
        try
        {
            var fi = new FileInfo(videoPath);
            if (!fi.Exists) return fullArt;
            Directory.CreateDirectory(Folder);
            var key = KeyFor(videoPath, fi.Length, fi.LastWriteTimeUtc);

            if (fullArt is not { Length: > 0 })
            {
                File.WriteAllBytes(Path.Combine(Folder, key + ".none"), Array.Empty<byte>());
                return null;
            }

            var thumb = MakeThumb(fullArt) ?? fullArt;
            File.WriteAllBytes(Path.Combine(Folder, key + ".jpg"), thumb);
            return thumb;
        }
        catch { return fullArt; }   // cache is an optimisation — never fail the caller
    }

    /// <summary>Downscales to <see cref="ThumbHeight"/> px tall JPEG (quality 85). Thread-safe.</summary>
    private static byte[]? MakeThumb(byte[] art)
    {
        try
        {
            using var input = new MemoryStream(art);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption       = BitmapCacheOption.OnLoad;
            bmp.StreamSource      = input;
            bmp.DecodePixelHeight = ThumbHeight;
            bmp.EndInit();
            bmp.Freeze();

            var enc = new JpegBitmapEncoder { QualityLevel = 85 };
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using var output = new MemoryStream();
            enc.Save(output);
            return output.ToArray();
        }
        catch { return null; }   // undecodable art — caller keeps the original bytes
    }

    /// <summary>
    /// Build 171: deletes thumbnails that no current library file uses — left behind when
    /// a file is re-embedded (new size/modified time = new key), renamed, moved or deleted.
    /// Returns (files removed, bytes freed).
    /// </summary>
    public static (int removed, long bytes) Prune(ISet<string> liveKeys)
    {
        int removed = 0; long bytes = 0;
        try
        {
            if (!Directory.Exists(Folder)) return (0, 0);
            foreach (var f in ThumbCacheKeys.StaleFiles(Directory.EnumerateFiles(Folder), liveKeys))
            {
                try
                {
                    var len = new FileInfo(f).Length;
                    File.Delete(f);
                    removed++; bytes += len;
                }
                catch { /* locked — try again next scan */ }
            }
        }
        catch { /* cache housekeeping is best-effort */ }
        return (removed, bytes);
    }

    /// <summary>Deletes all cached thumbnails (they rebuild on the next scan).</summary>
    public static int Clear()
    {
        int n = 0;
        try
        {
            if (!Directory.Exists(Folder)) return 0;
            foreach (var f in Directory.EnumerateFiles(Folder))
            {
                try { File.Delete(f); n++; } catch { /* locked — skip */ }
            }
        }
        catch { }
        return n;
    }
}
