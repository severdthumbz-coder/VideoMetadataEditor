using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace VideoMetadataEditor.Services;

/// <summary>
/// WPF-free half of the poster thumbnail cache: cache keys and deciding which cached
/// thumbnails are stale (build 171). Kept separate so it's unit-tested.
/// </summary>
public static class ThumbCacheKeys
{
    /// <summary>Key for one exact version of a video: path + size + last-write time.</summary>
    public static string KeyFor(string path, long size, DateTime lastWriteUtc)
    {
        var raw = $"{path.ToUpperInvariant()}|{size}|{lastWriteUtc.Ticks}";
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    /// <summary>Key for the file as it is on disk right now, or null if it can't be read.</summary>
    public static string? KeyForFile(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists ? KeyFor(path, fi.Length, fi.LastWriteTimeUtc) : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// From the files in the thumbs folder, the ones no current library file uses:
    /// thumbnails (*.jpg) and no-artwork markers (*.none) whose key isn't in
    /// <paramref name="liveKeys"/>. Anything else in the folder is left alone.
    /// </summary>
    public static List<string> StaleFiles(IEnumerable<string> cacheFiles, ISet<string> liveKeys)
    {
        var stale = new List<string>();
        foreach (var f in cacheFiles)
        {
            var ext = Path.GetExtension(f);
            if (!ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".none", StringComparison.OrdinalIgnoreCase)) continue;
            if (!liveKeys.Contains(Path.GetFileNameWithoutExtension(f))) stale.Add(f);
        }
        return stale;
    }
}
