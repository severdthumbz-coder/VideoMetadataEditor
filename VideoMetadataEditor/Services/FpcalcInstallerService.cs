using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace VideoMetadataEditor.Services;

/// <summary>
/// Manages fpcalc.exe installation and updates.
///
/// On first use (or when outdated):
///   1. Queries GitHub API for the latest Chromaprint release
///   2. Downloads the Windows x64 zip
///   3. Extracts fpcalc.exe to AppDir\native\
///
/// Version checking: runs "fpcalc --version" and compares to the GitHub tag.
/// Runs silently in the background — never blocks startup.
///
/// GitHub API: https://api.github.com/repos/acoustid/chromaprint/releases/latest
/// </summary>
public static class FpcalcInstallerService
{
    private const string GitHubApiUrl =
        "https://api.github.com/repos/acoustid/chromaprint/releases/latest";
    private const string UserAgent =
        "VideoMetadataEditor/1.4 (fpcalc auto-updater; contact@videometadataeditor.app)";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(30),
        DefaultRequestHeaders = { { "User-Agent", UserAgent } }
    };

    // ── Public result types ───────────────────────────────────────────────────

    public enum InstallResult
    {
        AlreadyCurrent,
        Installed,
        Updated,
        Failed,
        NoAssetFound
    }

    public record ReleaseInfo(string Version, string DownloadUrl, long SizeBytes);

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Gets the version string of the installed fpcalc.exe.
    /// Returns null if fpcalc is not installed or can't be run.
    /// e.g. returns "1.5.1" for "fpcalc version 1.5.1"
    /// </summary>
    public static async Task<string?> GetInstalledVersionAsync()
    {
        var fpcalc = Path.Combine(NativeLibraryExtractor.NativeDir, "fpcalc.exe");
        if (!File.Exists(fpcalc)) return null;

        try
        {
            using var proc = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName               = fpcalc,
                    // fpcalc supports both -version and --version on different builds.
                    // -version works on the official Chromaprint Windows builds.
                    Arguments              = "-version",
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true,
                }
            };
            proc.Start();
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            // Output examples:
            //   "fpcalc version 1.6.0"     (newer builds)
            //   "fpcalc version 1.5.1\n"   (older builds)
            //   sometimes printed to stderr instead of stdout
            var combined = (stdout + " " + stderr).Trim();
            var parts    = combined.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // Find the first token that parses as a version
            foreach (var p in parts.Reverse())
                if (System.Version.TryParse(p.Trim('v', 'V'), out _))
                    return p.Trim('v', 'V');
            return null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Queries GitHub for the latest Chromaprint release.
    /// Returns null on network error or if the Windows x64 asset isn't found.
    /// </summary>
    public static async Task<ReleaseInfo?> GetLatestReleaseAsync(
        CancellationToken ct = default, bool forceRefresh = false)
    {
        // Reuse a recent lookup so routine launches don't spend GitHub's API limit.
        var cached = ToolReleaseCache.Get("fpcalc", forceRefresh);
        if (cached != null)
            return new ReleaseInfo(cached.Version, cached.DownloadUrl, cached.SizeBytes);

        try
        {
            using var resp = await _http.GetAsync(GitHubApiUrl, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tagName = root.GetProperty("tag_name").GetString() ?? string.Empty;
            // Tags are like "v1.5.1" — strip leading 'v'
            var version = tagName.TrimStart('v');

            // Find the Windows x64 asset
            var assets = root.GetProperty("assets");
            foreach (var asset in assets.EnumerateArray())
            {
                var name = asset.GetProperty("name").GetString() ?? string.Empty;
                // Match: chromaprint-fpcalc-1.5.1-windows-x86_64.zip
                if (name.Contains("windows", StringComparison.OrdinalIgnoreCase)
                    && name.Contains("x86_64", StringComparison.OrdinalIgnoreCase)
                    && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    var url  = asset.GetProperty("browser_download_url").GetString()!;
                    var size = asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0L;
                    ToolReleaseCache.Put("fpcalc", version, url, size, version);
                    return new ReleaseInfo(version, url, size);
                }
            }

            return null; // No Windows asset in this release
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[FpcalcInstaller] GitHub API error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Checks installed version against latest release and installs/updates if needed.
    /// Progress reports: (percent 0-100, status message).
    /// </summary>
    public static async Task<(InstallResult result, string message)> CheckAndInstallAsync(
        IProgress<(int pct, string status)>? progress = null,
        CancellationToken ct = default)
    {
        progress?.Report((0, "Checking for fpcalc.exe…"));

        var installed = await GetInstalledVersionAsync();
        progress?.Report((5, "Checking latest release on GitHub…"));

        // About to install: always use a fresh lookup, not a cached one.
        var latest = await GetLatestReleaseAsync(ct, forceRefresh: true);
        if (latest == null)
        {
            var msg = installed != null
                ? $"✓ fpcalc {installed} installed (couldn't check for updates — no internet?)"
                : "⚠ Couldn't reach GitHub — check your internet connection.";
            progress?.Report((100, msg));
            return (InstallResult.Failed, msg);
        }

        if (installed != null && IsVersionSameOrNewer(installed, latest.Version))
        {
            var msg = $"✓ fpcalc {installed} is up to date.";
            progress?.Report((100, msg));
            return (InstallResult.AlreadyCurrent, msg);
        }

        var action   = installed == null ? "Installing" : $"Updating {installed} →";
        var sizeDesc = latest.SizeBytes > 0
            ? $" ({latest.SizeBytes / 1048576.0:F1} MB)" : string.Empty;

        progress?.Report((10,
            $"{action} fpcalc {latest.Version}{sizeDesc}…"));

        var (ok, errMsg) = await DownloadAndExtractAsync(latest, progress, ct);
        if (!ok)
        {
            var msg = $"⚠ Install failed: {errMsg}";
            progress?.Report((100, msg));
            return (InstallResult.Failed, msg);
        }

        var successMsg = installed == null
            ? $"✓ fpcalc {latest.Version} installed successfully."
            : $"✓ fpcalc updated to {latest.Version}.";
        progress?.Report((100, successMsg));
        return (installed == null ? InstallResult.Installed : InstallResult.Updated,
                successMsg);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static async Task<(bool ok, string? error)> DownloadAndExtractAsync(
        ReleaseInfo release,
        IProgress<(int pct, string status)>? progress,
        CancellationToken ct)
    {
        try
        {
            // Download entirely into memory — avoids writing a temp file to disk
            // which prevents Windows Defender from locking the zip before we extract it.
            progress?.Report((15, $"Downloading chromaprint {release.Version}…"));

            using var resp = await _http.GetAsync(
                release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            var total    = resp.Content.Headers.ContentLength ?? release.SizeBytes;
            var received = 0L;
            var memStream = new MemoryStream(total > 0 ? (int)total : 4 * 1024 * 1024);

            await using var httpStream = await resp.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[65536];
            int read;
            while ((read = await httpStream.ReadAsync(buffer, ct)) > 0)
            {
                memStream.Write(buffer, 0, read);
                received += read;
                if (total > 0)
                {
                    var pct = (int)(15 + received * 70 / total);
                    progress?.Report((pct,
                        $"Downloading… {received / 1048576.0:F1}/{total / 1048576.0:F1} MB"));
                }
            }

            // Extract fpcalc.exe directly from the in-memory zip stream
            progress?.Report((87, "Extracting fpcalc.exe…"));

            memStream.Position = 0;
            using var zip = new System.IO.Compression.ZipArchive(
                memStream, System.IO.Compression.ZipArchiveMode.Read, leaveOpen: false);

            // The zip contains a subdirectory: chromaprint-fpcalc-1.6.0-windows-x86_64/fpcalc.exe
            // Match by Name (filename only) not FullName (path) so any version's folder works
            var entry = zip.Entries.FirstOrDefault(e =>
                e.Name.Equals("fpcalc.exe", StringComparison.OrdinalIgnoreCase));

            if (entry == null)
                return (false,
                    $"fpcalc.exe not found in the zip. " +
                    $"Entries: {string.Join(", ", zip.Entries.Select(e => e.FullName))}");

            Directory.CreateDirectory(NativeLibraryExtractor.NativeDir);
            var destPath = Path.Combine(NativeLibraryExtractor.NativeDir, "fpcalc.exe");
            var tempDest = destPath + ".tmp";

            // Write via temp file → atomic replace (safe for running fpcalc replacement)
            using (var entryStream = entry.Open())
            await using (var destStream = new FileStream(tempDest, FileMode.Create,
                FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                await entryStream.CopyToAsync(destStream, ct);
            }
            File.Move(tempDest, destPath, overwrite: true);

            // Strip Mark-of-the-Web (Zone.Identifier alternate data stream).
            // Windows tags files extracted from internet-downloaded zips with this,
            // which can block execution under SmartScreen / AppLocker.
            try { File.Delete(destPath + ":Zone.Identifier"); } catch { }

            // Brief settle delay — give Defender's real-time scan a moment to finish
            // before we try to run the file ourselves. Without this the spawn can
            // race the AV scan and fail with sharing/access violations.
            await Task.Delay(500, ct);

            progress?.Report((95, "Verifying installation…"));

            var (verifiedVersion, runError) = await RunFpcalcAndCaptureErrorAsync(destPath);
            if (string.IsNullOrEmpty(verifiedVersion))
            {
                // Do NOT delete the file — the user may want to inspect it or
                // unblock it manually. Diagnostic info is the priority here.
                var detail = string.IsNullOrEmpty(runError)
                    ? "no output from --version"
                    : runError;
                return (false,
                    $"fpcalc.exe was extracted to native\\ but verification failed: {detail}. " +
                    "Try right-click → Properties → Unblock, or add an antivirus exclusion " +
                    "for the native\\ folder, then click Install again.");
            }

            return (true, null);
        }
        catch (OperationCanceledException) { return (false, "Cancelled."); }
        catch (Exception ex)              { return (false, ex.Message); }
    }

    /// <summary>
    /// Runs fpcalc --version and captures both stdout and stderr so the user
    /// gets a real error message instead of a vague "failed to run".
    /// </summary>
    private static async Task<(string? version, string? error)> RunFpcalcAndCaptureErrorAsync(
        string fpcalcPath)
    {
        try
        {
            using var proc = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName               = fpcalcPath,
                    Arguments              = "-version",
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true,
                }
            };
            proc.Start();
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            // fpcalc -version output: "fpcalc version 1.6.0"
            var combined = (stdout + " " + stderr).Trim();
            var parts    = combined.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // Find the version token (last numeric.numeric.numeric chunk)
            var version = parts.LastOrDefault(p =>
                System.Version.TryParse(p, out _));

            if (!string.IsNullOrEmpty(version))
                return (version, null);

            // Process ran but no version found — return stderr or stdout for diagnosis
            return (null,
                !string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() :
                !string.IsNullOrWhiteSpace(stdout) ? stdout.Trim() :
                $"exit code {proc.ExitCode}");
        }
        catch (System.ComponentModel.Win32Exception wex)
        {
            // Common Windows error codes during exec
            return (null, $"Windows error {wex.NativeErrorCode}: {wex.Message}");
        }
        catch (Exception ex)
        {
            return (null, ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>
    /// Returns true if installedVersion is >= requiredVersion.
    /// Uses simple numeric comparison on Major.Minor.Patch segments.
    /// </summary>
    private static bool IsVersionSameOrNewer(string installed, string latest)
    {
        return System.Version.TryParse(installed, out var a)
            && System.Version.TryParse(latest,    out var b)
            && a >= b;
    }
}
