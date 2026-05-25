using System.IO;
using System.Text.Json;

namespace VideoMetadataEditor.Services;

/// <summary>
/// Persists audio fingerprints to disk so files only need to be decoded once.
/// Cache file: fingerprints.json alongside the app EXE.
///
/// Cache key: file path + file size + last-write UTC (same strategy as LibraryCacheService).
/// On mismatch (file changed) the old entry is silently replaced.
///
/// Thread-safe: all public methods lock on _lock.
/// </summary>
public class FingerprintCacheService
{
    private record CacheEntry(
        string   FilePath,
        long     FileSizeBytes,
        DateTime LastWriteUtc,
        string?  Fingerprint,       // null = "file has no audio / decode failed"
        DateTime ComputedAt);

    private readonly string  _cachePath;
    private readonly object  _lock = new();
    private Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private bool _dirty;

    public FingerprintCacheService()
    {
        _cachePath = Path.Combine(
            Path.GetDirectoryName(Environment.ProcessPath)
                ?? AppContext.BaseDirectory,
            "fingerprints.json");
        Load();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Returns cached fingerprint, or null if not cached / stale.</summary>
    public string? TryGet(string filePath)
    {
        lock (_lock)
        {
            if (!_cache.TryGetValue(filePath, out var entry)) return null;
            if (!IsFresh(filePath, entry))
            {
                _cache.Remove(filePath);
                _dirty = true;
                return null;
            }
            return entry.Fingerprint ?? string.Empty; // empty string = "no audio"
        }
    }

    /// <summary>
    /// Stores a fingerprint. Pass null fingerprint when the file has no audio
    /// (stored as empty string so we don't re-decode it next time).
    /// </summary>
    public void Store(string filePath, string? fingerprint)
    {
        lock (_lock)
        {
            try
            {
                var info = new FileInfo(filePath);
                _cache[filePath] = new CacheEntry(
                    filePath, info.Length, info.LastWriteTimeUtc,
                    fingerprint, DateTime.UtcNow);
                _dirty = true;
            }
            catch { /* file moved/deleted between decode and store */ }
        }
    }

    /// <summary>
    /// Returns true if the file has been processed and has no audio track.
    /// Used to skip re-decoding files that are known to be audio-free.
    /// </summary>
    public bool IsKnownNoAudio(string filePath)
    {
        lock (_lock)
        {
            if (!_cache.TryGetValue(filePath, out var entry)) return false;
            return IsFresh(filePath, entry) && entry.Fingerprint == string.Empty;
        }
    }

    /// <summary>Saves cache to disk if any entries have changed.</summary>
    public void SaveIfDirty()
    {
        lock (_lock)
        {
            if (!_dirty) return;
            try
            {
                var json = JsonSerializer.Serialize(_cache.Values.ToList(),
                    new JsonSerializerOptions { WriteIndented = false });
                File.WriteAllText(_cachePath, json);
                _dirty = false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[FingerprintCache] Save failed: {ex.Message}");
            }
        }
    }

    /// <summary>Clears all cached entries from memory (does not delete the file).</summary>
    public void ClearMemory()
    {
        lock (_lock)
        {
            _cache.Clear();
            _dirty = true;
        }
    }

    /// <summary>
    /// Merges the cache file on disk into the in-memory state without requiring
    /// a restart. Entries already in memory that are not on disk are kept.
    /// Called after a cache import so the app doesn't need to restart.
    /// </summary>
    public void Reload()
    {
        lock (_lock)
        {
            // Snapshot what's in memory (freshly computed since last save)
            var inMemoryOnly = new Dictionary<string, CacheEntry>(
                _cache, StringComparer.OrdinalIgnoreCase);
            _cache.Clear();
            Load();   // reads new file into _cache
            // Re-add anything that was only in memory (don't lose fresh work)
            foreach (var kvp in inMemoryOnly)
                _cache.TryAdd(kvp.Key, kvp.Value);
            _dirty = false;
        }
    }

    // ── Private ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Merges entries from an external cache file into the in-memory state.
    /// Existing entries are kept; imported entries only fill gaps (don't overwrite).
    /// </summary>
    public void MergeFrom(string filePath)
    {
        lock (_lock)
        {
            try
            {
                var json    = File.ReadAllText(filePath);
                var entries = JsonSerializer.Deserialize<List<CacheEntry>>(json);
                if (entries == null) return;
                foreach (var entry in entries)
                    _cache.TryAdd(entry.FilePath, entry);   // don't overwrite fresher local entries
                _dirty = true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[FingerprintCache] MergeFrom failed: {ex.Message}");
            }
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_cachePath)) return;
            var json    = File.ReadAllText(_cachePath);
            var entries = JsonSerializer.Deserialize<List<CacheEntry>>(json);
            if (entries == null) return;
            _cache = entries
                .DistinctBy(e => e.FilePath, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(e => e.FilePath, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[FingerprintCache] Load failed: {ex.Message}");
        }
    }

    private static bool IsFresh(string filePath, CacheEntry entry)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists) return false;
            return info.Length == entry.FileSizeBytes
                && Math.Abs((info.LastWriteTimeUtc - entry.LastWriteUtc).TotalSeconds) <= 2;
        }
        catch { return false; }
    }
}
