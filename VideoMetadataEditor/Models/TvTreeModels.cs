using System.Collections.Generic;
using System.Linq;

namespace VideoMetadataEditor.Models;

/// <summary>Season node in the TV Library tree view.</summary>
public class TvSeasonNode
{
    public int     SeasonNumber { get; set; }
    public string  SeasonLabel  { get; set; } = string.Empty;

    public List<LibraryEntry> Episodes { get; set; } = new();

    // ── Computed ─────────────────────────────────────────────────────────────
    public string Label
    {
        get
        {
            var name  = SeasonNumber == 0 ? "Specials" : $"Season {SeasonNumber:D2}";
            var real  = Episodes.Count(e => !e.IsMissingEpisode);
            var total = Episodes.Count;
            var miss  = total - real;
            return miss > 0
                ? $"{name}  ({real} eps · {miss} missing)"
                : $"{name}  ({real} episode{(real == 1 ? "" : "s")})";
        }
    }

    public string WatchedLabel
    {
        get
        {
            var real    = Episodes.Where(e => !e.IsMissingEpisode).ToList();
            var watched = real.Count(e => e.IsWatched);
            return watched == real.Count ? "✓ All watched"
                 : watched == 0          ? string.Empty
                 : $"{watched}/{real.Count} watched";
        }
    }

    public bool HasMissing => Episodes.Any(e => e.IsMissingEpisode);
}

/// <summary>Show node in the TV Library tree view.</summary>
public class TvShowNode
{
    public string ShowTitle     { get; set; } = string.Empty;
    public List<TvSeasonNode> Seasons { get; set; } = new();

    // ── Computed ─────────────────────────────────────────────────────────────
    public int EpisodeCount   => Seasons?.Sum(s => s.Episodes?.Count(e => !e.IsMissingEpisode) ?? 0) ?? 0;
    public int WatchedCount   => Seasons?.Sum(s => s.Episodes?.Count(e => e.IsWatched && !e.IsMissingEpisode) ?? 0) ?? 0;
    public int MissingCount   => Seasons?.Sum(s => s.Episodes?.Count(e => e.IsMissingEpisode) ?? 0) ?? 0;

    public string WatchedSummary
    {
        get
        {
            if (EpisodeCount == 0) return string.Empty;
            if (WatchedCount == EpisodeCount) return " ✓";
            if (WatchedCount == 0) return string.Empty;
            return $"  {WatchedCount}/{EpisodeCount} watched";
        }
    }

    public string MissingSummary => MissingCount > 0
        ? $"  ⚠ {MissingCount} missing" : string.Empty;

    public string EpisodeSummary
    {
        get
        {
            var parts = new List<string>();
            parts.Add($"{EpisodeCount} episode{(EpisodeCount == 1 ? "" : "s")}");
            if (WatchedCount > 0 && WatchedCount < EpisodeCount)
                parts.Add($"{WatchedCount} watched");
            else if (WatchedCount == EpisodeCount && EpisodeCount > 0)
                parts.Add("all watched ✓");
            if (MissingCount > 0)
                parts.Add($"⚠ {MissingCount} missing");
            return string.Join(" · ", parts);
        }
    }

    public string TotalDuration
    {
        get
        {
            try
            {
                var allEps = Seasons?.SelectMany(s => s.Episodes ?? new())
                    .Where(e => !e.IsMissingEpisode && !string.IsNullOrWhiteSpace(e.Duration))
                    .ToList() ?? new();
                if (allEps.Count == 0) return string.Empty;
                var totalSecs = allEps.Sum(e =>
                {
                    var parts = e.Duration.Split(':');
                    if (parts.Length == 3 &&
                        int.TryParse(parts[0], out int h) &&
                        int.TryParse(parts[1], out int m) &&
                        int.TryParse(parts[2], out int s))
                        return h * 3600 + m * 60 + s;
                    return 0;
                });
                var ts = System.TimeSpan.FromSeconds(totalSecs);
                return ts.TotalHours >= 1
                    ? $"{(int)ts.TotalHours}h {ts.Minutes}m"
                    : $"{ts.Minutes}m";
            }
            catch { return string.Empty; }
        }
    }

    public long TotalSizeBytes => Seasons?
        .SelectMany(s => s.Episodes ?? new())
        .Where(e => !e.IsMissingEpisode)
        .Sum(e => e.FileSizeBytes) ?? 0;

    public string TotalSizeDisplay
    {
        get
        {
            var b = TotalSizeBytes;
            if (b <= 0) return string.Empty;
            if (b >= 1_073_741_824) return $"{b / 1_073_741_824.0:F1} GB";
            if (b >= 1_048_576)     return $"{b / 1_048_576.0:F1} MB";
            return $"{b / 1024.0:F1} KB";
        }
    }

    public string TooltipText
    {
        get
        {
            var parts = new List<string> { ShowTitle };
            if (!string.IsNullOrEmpty(TotalDuration))  parts.Add($"Total runtime: {TotalDuration}");
            if (!string.IsNullOrEmpty(TotalSizeDisplay)) parts.Add($"Total size: {TotalSizeDisplay}");
            if (WatchedCount > 0) parts.Add($"Watched: {WatchedCount}/{EpisodeCount}");
            if (MissingCount > 0) parts.Add($"Missing episodes: {MissingCount}");
            return string.Join("\n", parts);
        }
    }

    // ── Cover art ─────────────────────────────────────────────────────────────
    private byte[]? _coverArt;
    public byte[]? CoverArt
    {
        get => _coverArt;
        set { _coverArt = value; _coverSource = null; }
    }

    // Decoded posters are cached by the identity of their source byte[] so that the
    // many TvShowNode instances rebuilt each time the tree getter runs reuse the same
    // frozen BitmapSource instead of re-decoding the JPEG on the UI thread. The cover
    // byte[] comes from a stable LibraryEntry, so reference identity is a valid key.
    // ConditionalWeakTable lets the cached bitmap be collected once the bytes are gone.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[], System.Windows.Media.Imaging.BitmapSource> _coverCache = new();

    private System.Windows.Media.Imaging.BitmapSource? _coverSource;
    public System.Windows.Media.Imaging.BitmapSource? CoverSource
    {
        get
        {
            if (_coverSource != null) return _coverSource;
            if (_coverArt is not { Length: > 0 }) return null;

            // Reuse a previously-decoded poster for these exact bytes if we have one.
            if (_coverCache.TryGetValue(_coverArt, out var cached))
            {
                _coverSource = cached;
                return _coverSource;
            }

            try
            {
                using var ms = new System.IO.MemoryStream(_coverArt);
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption     = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.StreamSource    = ms;
                bmp.DecodePixelWidth = 72;
                bmp.EndInit();
                bmp.Freeze();
                _coverSource = bmp;
                _coverCache.AddOrUpdate(_coverArt, bmp);
            }
            catch { /* BitmapImage decode failed — CoverSource stays null */ }
            return _coverSource;
        }
    }
}
