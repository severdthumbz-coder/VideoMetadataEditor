using System.IO;

namespace VideoMetadataEditor.Services;

/// <summary>
/// Recommends how many files to write concurrently during batch processing,
/// based on CPU count, drive type, and TagLib# write characteristics.
///
/// TagLib# write behaviour:
///   - Each write is mostly CPU-bound (XML/binary tag serialisation, JPEG
///     compression for artwork) with a short IO burst to flush the file.
///   - The temp-copy method doubles the IO: one SysFile.Copy + one tag write.
///   - NVMe/SSD can sustain many concurrent tag flushes; HDD cannot.
///   - TagLib# is NOT thread-safe per-instance but the service creates a new
///     instance per file, so concurrent calls on different files are safe.
///
/// Worker count bands (TagLib# + temp-copy):
///   NVMe internal    : min(CPUs, 8)    — CPU-bound, fast flush
///   SATA/internal SSD: min(CPUs, 6)    — fast flush, slight queue overhead
///   USB SSD          : min(CPUs, 4)    — USB bandwidth shared with read+write
///   HDD (any)        : 2              — seek penalty makes more workers hurt
///   Network          : 1              — write locking on network shares
///   Unknown          : min(CPUs, 4)   — safe SSD-like default
/// </summary>
public static class WriteThreadService
{
    /// <summary>
    /// Returns the recommended number of concurrent writes for files on
    /// <paramref name="samplePath"/>. Clamps to [1, <paramref name="userMax"/>].
    /// </summary>
    public static int RecommendedWorkers(string samplePath, int userMax = int.MaxValue)
    {
        try
        {
            var profile = DriveCapabilityService.GetProfile(samplePath);
            int cpus    = Environment.ProcessorCount;

            int workers = profile switch
            {
                { IsNetwork: true }            => 1,
                { IsRemovable: true, IsSsd: false } => 2,       // USB HDD
                { IsRemovable: true, IsSsd: true  } => Math.Min(cpus, 4),  // USB SSD
                { IsNvme: true }               => Math.Min(cpus, 8),
                { IsSsd: true }                => Math.Min(cpus, 6),
                _                              => 2              // HDD fallback
            };

            return Math.Clamp(workers, 1, Math.Max(1, userMax));
        }
        catch
        {
            return Math.Clamp(Math.Min(Environment.ProcessorCount, 4), 1, Math.Max(1, userMax));
        }
    }

    /// <summary>
    /// Returns a human-readable description of the recommended setting.
    /// </summary>
    public static string Describe(string samplePath)
    {
        try
        {
            var profile = DriveCapabilityService.GetProfile(samplePath);
            int workers = RecommendedWorkers(samplePath);
            return $"{profile.Description.Split('—')[0].Trim()} → {workers} concurrent write(s)";
        }
        catch
        {
            return $"{RecommendedWorkers(samplePath)} concurrent write(s) (default)";
        }
    }
}
