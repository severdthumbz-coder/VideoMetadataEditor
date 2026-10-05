using VideoMetadataEditor.Models;

namespace VideoMetadataEditor.Services;

/// <summary>
/// Small persisted cache for "latest release" lookups that hit GitHub's API
/// (fpcalc, FFmpeg BtbN). GitHub allows ~60 anonymous API requests per hour per
/// computer; without a cache every VME launch spent several of them, which could
/// crowd out the app's own update check.
///
/// Normal lookups reuse an entry for up to 12 hours. A forced lookup (the user
/// clicked "Check all for updates" or Download) only reuses an entry fetched in the
/// last minute — enough to avoid fetching twice within one refresh.
/// </summary>
public static class ToolReleaseCache
{
    public static readonly TimeSpan NormalMaxAge = TimeSpan.FromHours(12);
    public static readonly TimeSpan ForcedMaxAge = TimeSpan.FromMinutes(1);

    private static readonly object _lock = new();

    /// <summary>Returns a cached entry for <paramref name="key"/> if it is recent enough, else null.</summary>
    public static ToolReleaseCacheEntry? Get(string key, bool forceRefresh)
    {
        var maxAge = forceRefresh ? ForcedMaxAge : NormalMaxAge;
        lock (_lock)
        {
            var cache = App.ConfigService?.Settings?.ToolReleaseCache;
            if (cache == null || !cache.TryGetValue(key, out var entry) || entry == null) return null;
            if (string.IsNullOrWhiteSpace(entry.Version)) return null;
            return DateTime.UtcNow - entry.FetchedUtc <= maxAge ? entry : null;
        }
    }

    /// <summary>Stores a fresh lookup result and saves settings in the background.</summary>
    public static void Put(string key, string version, string downloadUrl, long sizeBytes, string releaseId)
    {
        lock (_lock)
        {
            var settings = App.ConfigService?.Settings;
            if (settings == null) return;
            settings.ToolReleaseCache ??= new();
            settings.ToolReleaseCache[key] = new ToolReleaseCacheEntry
            {
                Version     = version,
                DownloadUrl = downloadUrl,
                SizeBytes   = sizeBytes,
                ReleaseId   = releaseId,
                FetchedUtc  = DateTime.UtcNow
            };
        }
        _ = App.ConfigService!.SaveAsync();
    }
}
