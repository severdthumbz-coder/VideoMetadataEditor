using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace VideoMetadataEditor.Services;

/// <summary>
/// Downloads and installs a stable Windows ffmpeg build into the portable
/// native\ folder. Mirrors <see cref="FpcalcInstallerService"/>'s hardened
/// pattern: in-memory download (so Windows Defender can't lock a temp zip),
/// atomic temp→replace, Mark-of-the-Web stripping, settle delay, and a
/// post-install --version verification.
///
/// Source: BtbN/FFmpeg-Builds GitHub releases. The user asked for stable over
/// bleeding-edge, so we take the latest *release* (not the rolling "latest"
/// master pre-release) and pick the win64-gpl, non-shared asset — a single
/// self-contained ffmpeg.exe once extracted.
/// </summary>
public static class FfmpegInstallerService
{
    // "releases/latest" returns the newest NON-prerelease release — i.e. a
    // stable tagged build, not the rolling master autobuild.
    private const string GitHubApiUrl =
        "https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/latest";
    private const string UserAgent =
        "VideoMetadataEditor/1.4 (ffmpeg auto-updater; contact@videometadataeditor.app)";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(3), // ffmpeg zips are ~30–90 MB
        DefaultRequestHeaders = { { "User-Agent", UserAgent } }
    };

    public enum InstallResult { AlreadyCurrent, Installed, Updated, Failed, NoAssetFound }

    /// <summary>
    /// A BtbN release. <see cref="ReleaseId"/> is the release's publish timestamp —
    /// a stable identity we stamp after install and compare against, so we never have
    /// to parse BtbN's non-semver build strings (e.g. "N-127203-ga35c879992") to decide
    /// whether an update exists.
    /// </summary>
    public record ReleaseInfo(string Version, string DownloadUrl, long SizeBytes, string ReleaseId);

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Installed ffmpeg version string (build-flavoured), or null.</summary>
    public static Task<string?> GetInstalledVersionAsync() => FfmpegService.GetVersionAsync();

    /// <summary>
    /// Queries BtbN for the latest stable release and finds the win64-gpl
    /// (non-shared) zip. Returns null on network error or if no asset matches.
    /// </summary>
    public static async Task<ReleaseInfo?> GetLatestReleaseAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(GitHubApiUrl, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tagName = root.GetProperty("tag_name").GetString() ?? string.Empty;
            // Tags look like "autobuild-2024-..." for master or "n7.1" for stable.
            // Strip a leading 'n' so "n7.1" → "7.1" where possible.
            var version = tagName.StartsWith("n", StringComparison.OrdinalIgnoreCase)
                ? tagName[1..] : tagName;

            // Stable identity for "up to date" checks: publish timestamp, else tag.
            var releaseId = root.TryGetProperty("published_at", out var pub)
                ? (pub.GetString() ?? tagName) : tagName;

            var assets = root.GetProperty("assets");
            // Prefer: win64, gpl, NOT shared, NOT lgpl-only, .zip
            // e.g. ffmpeg-n7.1-latest-win64-gpl-7.1.zip
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? string.Empty;
                var lower = name.ToLowerInvariant();
                if (lower.Contains("win64")
                    && lower.Contains("gpl")
                    && !lower.Contains("shared")
                    && lower.EndsWith(".zip"))
                {
                    var url  = asset.GetProperty("browser_download_url").GetString()!;
                    var size = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0L;
                    return new ReleaseInfo(version, url, size, releaseId);
                }
            }

            return null;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[FfmpegInstaller] GitHub API error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Checks installed vs latest and installs/updates when needed.
    /// Because ffmpeg build strings vary by distributor (gyan vs BtbN), a clean
    /// version comparison isn't always possible — when it can't be made, this
    /// treats an existing install as current and only offers a manual reinstall.
    /// </summary>
    public static async Task<(InstallResult result, string message)> CheckAndInstallAsync(
        IProgress<(int pct, string status)>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report((0, "Checking for ffmpeg.exe…"));
        var installed = await GetInstalledVersionAsync();

        progress?.Report((5, "Checking latest release on GitHub…"));
        var latest = await GetLatestReleaseAsync(ct);
        if (latest == null)
        {
            var msg = installed != null
                ? $"✓ ffmpeg {installed} installed (couldn't check for updates — no internet, or no matching asset?)"
                : "⚠ Couldn't reach GitHub or find a Windows build — check your connection.";
            progress?.Report((100, msg));
            return (InstallResult.Failed, msg);
        }

        // "Up to date" when an ffmpeg is present AND the build VME last installed
        // matches the current latest release (compared by stable release id, so
        // BtbN's non-semver "N-..." strings never cause a false "update available").
        var stampedId = App.ConfigService.Settings.FfmpegInstalledReleaseId;
        if (installed != null
            && !string.IsNullOrEmpty(stampedId)
            && stampedId == latest.ReleaseId)
        {
            var msg = $"✓ ffmpeg {installed} is up to date.";
            progress?.Report((100, msg));
            return (InstallResult.AlreadyCurrent, msg);
        }

        var action   = installed == null ? "Installing" : $"Updating {installed} →";
        var sizeDesc = latest.SizeBytes > 0 ? $" ({latest.SizeBytes / 1048576.0:F1} MB)" : string.Empty;
        progress?.Report((10, $"{action} ffmpeg {latest.Version}{sizeDesc}…"));

        var (ok, errMsg) = await DownloadAndExtractAsync(latest, progress, ct);
        if (!ok)
        {
            var msg = $"⚠ Install failed: {errMsg}";
            progress?.Report((100, msg));
            return (InstallResult.Failed, msg);
        }

        // Stamp the installed release identity so the next check reports "up to date".
        App.ConfigService.Settings.FfmpegInstalledReleaseId = latest.ReleaseId;
        _ = App.ConfigService.SaveAsync();

        var successMsg = installed == null
            ? $"✓ ffmpeg {latest.Version} installed successfully."
            : $"✓ ffmpeg updated to {latest.Version}.";
        progress?.Report((100, successMsg));
        return (installed == null ? InstallResult.Installed : InstallResult.Updated, successMsg);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static async Task<(bool ok, string? error)> DownloadAndExtractAsync(
        ReleaseInfo release,
        IProgress<(int pct, string status)>? progress,
        CancellationToken ct)
    {
        try
        {
            progress?.Report((15, $"Downloading ffmpeg {release.Version}…"));

            using var resp = await _http.GetAsync(
                release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            var total    = resp.Content.Headers.ContentLength ?? release.SizeBytes;
            var received = 0L;
            var memStream = new MemoryStream(total > 0 ? (int)total : 48 * 1024 * 1024);

            await using (var httpStream = await resp.Content.ReadAsStreamAsync(ct))
            {
                var buffer = new byte[65536];
                int read;
                while ((read = await httpStream.ReadAsync(buffer, ct)) > 0)
                {
                    memStream.Write(buffer, 0, read);
                    received += read;
                    if (total > 0)
                    {
                        var pct = (int)(15 + received * 65 / total);
                        progress?.Report((pct,
                            $"Downloading… {received / 1048576.0:F1}/{total / 1048576.0:F1} MB"));
                    }
                }
            }

            progress?.Report((82, "Extracting ffmpeg.exe…"));
            memStream.Position = 0;
            using var zip = new System.IO.Compression.ZipArchive(
                memStream, System.IO.Compression.ZipArchiveMode.Read, leaveOpen: false);

            // Zip layout: ffmpeg-<ver>-win64-gpl/bin/ffmpeg.exe — match by filename.
            var entry = zip.Entries.FirstOrDefault(e =>
                e.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase));
            if (entry == null)
                return (false,
                    "ffmpeg.exe not found in the downloaded zip. " +
                    $"Entries: {string.Join(", ", zip.Entries.Take(20).Select(e => e.FullName))}");

            Directory.CreateDirectory(NativeLibraryExtractor.NativeDir);
            var destPath = Path.Combine(NativeLibraryExtractor.NativeDir, "ffmpeg.exe");
            var tempDest = destPath + ".tmp";

            using (var entryStream = entry.Open())
            await using (var destStream = new FileStream(tempDest, FileMode.Create,
                FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                await entryStream.CopyToAsync(destStream, ct);
            }
            File.Move(tempDest, destPath, overwrite: true);

            // Strip Mark-of-the-Web so SmartScreen/AppLocker doesn't block execution.
            try { File.Delete(destPath + ":Zone.Identifier"); } catch { }

            // Let Defender's real-time scan settle before we run the file.
            await Task.Delay(500, ct);

            progress?.Report((92, "Verifying installation…"));
            FfmpegService.Detect(forceRedetect: true); // re-resolve to the new native\ exe
            var verified = await FfmpegService.GetVersionAsync();
            if (string.IsNullOrEmpty(verified))
                return (false,
                    "ffmpeg.exe was extracted to native\\ but verification failed. " +
                    "Try right-click → Properties → Unblock, or add an antivirus exclusion " +
                    "for the native\\ folder, then click Download again.");

            return (true, null);
        }
        catch (OperationCanceledException) { return (false, "Cancelled."); }
        catch (Exception ex)              { return (false, ex.Message); }
    }
}
