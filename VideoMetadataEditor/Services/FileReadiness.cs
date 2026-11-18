using System.IO;

namespace VideoMetadataEditor.Services;

/// <summary>
/// "Has this file finished arriving?" (build 176).
///
/// Folder watchers see a file the moment it's created — a move to the library, a copy
/// or a download is still writing it for seconds or minutes after that. Reading it then
/// gives empty tags and unreadable tracks (and auto-embed would write to a half-copied
/// file). A file is treated as ready when nobody has it open for writing (it can be
/// opened while denying writers) and its size hasn't changed between two checks.
/// WPF-free so it's unit-tested.
/// </summary>
public static class FileReadiness
{
    /// <summary>
    /// True when the file exists, has content, and no other process has it open for
    /// writing. Copy engines, Explorer, browsers and ffmpeg all hold write handles while
    /// they work, so this fails until they're done.
    /// </summary>
    public static bool IsReadyNow(string path)
    {
        try
        {
            // FileShare.Read = "others may read, nobody may write" — refused while a
            // writer still has the file open.
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return fs.Length > 0;
        }
        catch { return false; }
    }

    private static long SizeOf(string path)
    {
        try { return new FileInfo(path).Length; } catch { return -1; }
    }

    /// <summary>
    /// Waits until <paramref name="path"/> is ready (see <see cref="IsReadyNow"/>) and its size
    /// has stayed the same for one <paramref name="settle"/> interval. Calls
    /// <paramref name="onWaiting"/> once if it has to wait. Returns false if the file
    /// disappears or <paramref name="timeout"/> passes.
    /// </summary>
    public static async Task<bool> WaitUntilReadyAsync(string path, TimeSpan timeout,
        TimeSpan? settle = null, Action? onWaiting = null, CancellationToken ct = default)
    {
        var interval = settle ?? TimeSpan.FromSeconds(2);
        var deadline = DateTime.UtcNow + timeout;
        long lastSize = -2;
        bool told = false;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (!File.Exists(path)) return false;

            var size = SizeOf(path);
            if (size > 0 && size == lastSize && IsReadyNow(path)) return true;

            // First look is just a baseline; only report waiting once it's clearly busy.
            if (lastSize != -2 && !told) { told = true; onWaiting?.Invoke(); }
            lastSize = size;

            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(interval, ct).ConfigureAwait(false);
        }
    }
}
