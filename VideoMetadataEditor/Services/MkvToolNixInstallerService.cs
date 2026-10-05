using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VideoMetadataEditor.Services;

/// <summary>
/// Downloads the portable 64-bit MKVToolNix build and extracts it into
/// native\MKVToolNix\ so mkvpropedit (plus its required DLLs/data) is available
/// for proper Matroska cover-art embedding.
///
/// MKVToolNix ships only as a .7z archive, so this relies on <see cref="SevenZipService"/>
/// (a system 7-Zip, or VME's downloaded 7zr.exe). If no 7-Zip extractor is available,
/// the caller falls back to opening the MKVToolNix download page.
///
/// Unlike ffmpeg/fpcalc (single self-contained exe), mkvpropedit needs its sibling
/// files, so the WHOLE archive is extracted into native\MKVToolNix\.
/// </summary>
public static class MkvToolNixInstallerService
{
    // Primary: the static directory listing of Windows release folders (102.0/, 101.0/, …).
    // We pick the highest VERSION NUMBER — not the newest modified date, because the
    // mirror re-syncs every folder at once, so they all share the same timestamp.
    private const string ReleasesIndexUrl = "https://mkvtoolnix.download/windows/releases/";
    // Fallback: the official latest-release feed.
    private const string LatestXmlUrl = "https://mkvtoolnix.download/latest-release.xml";
    private const string UserAgent =
        "VideoMetadataEditor/1.4 (mkvtoolnix auto-updater; contact@videometadataeditor.app)";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(3),
        DefaultRequestHeaders = { { "User-Agent", UserAgent } }
    };

    public enum InstallResult { AlreadyCurrent, Installed, Updated, Failed, NoExtractor }

    /// <summary>
    /// <see cref="Version"/>/<see cref="DownloadUrl"/> are the newest release.
    /// <see cref="Fallbacks"/> holds the next-newest versions (descending) so the
    /// installer can step back if the newest folder exists but its .7z isn't uploaded yet.
    /// </summary>
    public record ReleaseInfo(string Version, string DownloadUrl, IReadOnlyList<string> Fallbacks);

    private static string SevenZipUrlFor(string version) =>
        $"https://mkvtoolnix.download/windows/releases/{version}/mkvtoolnix-64-bit-{version}.7z";

    /// <summary>Where the portable MKVToolNix folder is installed.</summary>
    public static string InstallDir =>
        Path.Combine(NativeLibraryExtractor.NativeDir, "MKVToolNix");

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Finds the newest MKVToolNix release. Tries the Windows releases directory
    /// listing first (highest version folder), then falls back to latest-release.xml.
    /// Returns null if neither source can be read.
    /// </summary>
    public static async Task<ReleaseInfo?> GetLatestReleaseAsync(CancellationToken ct = default)
    {
        var versions = await GetVersionsFromIndexAsync(ct);
        if (versions.Count == 0)
        {
            var fromXml = await GetVersionFromXmlAsync(ct);
            if (fromXml != null) versions.Add(fromXml);
        }
        if (versions.Count == 0) return null;

        var newest    = versions[0];
        var fallbacks = versions.Skip(1).Take(2).ToList();
        return new ReleaseInfo(newest, SevenZipUrlFor(newest), fallbacks);
    }

    /// <summary>
    /// Reads the releases directory listing and returns every version folder,
    /// sorted newest-first by numeric version (so 102.0 beats 99.0 and 1.7.0).
    /// </summary>
    private static async Task<List<string>> GetVersionsFromIndexAsync(CancellationToken ct)
    {
        var result = new List<string>();
        try
        {
            using var resp = await _http.GetAsync(ReleasesIndexUrl, ct);
            if (!resp.IsSuccessStatusCode) return result;
            var html = await resp.Content.ReadAsStringAsync(ct);

            // Folder links look like href="102.0/" or href="./102.0/". Only accept
            // links whose target is purely a version number followed by a slash.
            var seen = new HashSet<string>();
            foreach (Match m in Regex.Matches(html,
                         @"href\s*=\s*""(?:\./)?(\d+(?:\.\d+){1,2})/""", RegexOptions.IgnoreCase))
            {
                var v = m.Groups[1].Value;
                if (System.Version.TryParse(v, out _) && seen.Add(v)) result.Add(v);
            }

            result.Sort((a, b) => System.Version.Parse(b).CompareTo(System.Version.Parse(a)));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MkvToolNixInstaller] index error: {ex.Message}");
        }
        return result;
    }

    /// <summary>
    /// Fallback: reads latest-release.xml. Skips the &lt;?xml version="1.0"?&gt;
    /// declaration (Build 161 mistook that "1.0" for the latest release) and prefers
    /// an explicit &lt;version&gt; element.
    /// </summary>
    private static async Task<string?> GetVersionFromXmlAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(LatestXmlUrl, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var xml = await resp.Content.ReadAsStringAsync(ct);

            // Drop the XML declaration so its version="1.0" can never match.
            xml = Regex.Replace(xml, @"<\?xml[^>]*\?>", string.Empty);

            var tagged = Regex.Match(xml, @"<version>\s*(\d+(?:\.\d+){1,2})\s*</version>",
                                     RegexOptions.IgnoreCase);
            if (tagged.Success) return tagged.Groups[1].Value;

            // Last resort: the highest version-looking token in the document.
            var best = Regex.Matches(xml, @"\b(\d+\.\d+(?:\.\d+)?)\b")
                .Select(m => m.Groups[1].Value)
                .Where(v => System.Version.TryParse(v, out _))
                .OrderByDescending(v => System.Version.Parse(v))
                .FirstOrDefault();
            return best;
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MkvToolNixInstaller] xml error: {ex.Message}");
            return null;
        }
    }

    /// <summary>Installed mkvpropedit version number (e.g. "98.0"), or null.</summary>
    public static string? GetInstalledVersionNumber()
    {
        // MkvPropEditService.Version is a full line like "mkvpropedit v98.0 ('Chonks') 64-bit".
        var line = MkvPropEditService.Version;
        if (string.IsNullOrWhiteSpace(line)) return null;
        var m = Regex.Match(line, @"v?(\d+\.\d+(?:\.\d+)?)");
        return m.Success ? m.Groups[1].Value : null;
    }

    public static async Task<(InstallResult result, string message)> CheckAndInstallAsync(
        IProgress<(int pct, string status)>? progress = null, CancellationToken ct = default)
    {
        progress?.Report((0, "Checking MKVToolNix…"));

        if (!SevenZipService.IsAvailable)
            return (InstallResult.NoExtractor,
                "MKVToolNix is distributed as a .7z archive, which needs a 7-Zip extractor. " +
                "Install 7-Zip from the 7-Zip (7zr) row above (or your system 7-Zip), then try again — " +
                "or use Page to download MKVToolNix manually.");

        MkvPropEditService.Detect();
        var installed = GetInstalledVersionNumber();

        progress?.Report((5, "Checking latest release…"));
        var latest = await GetLatestReleaseAsync(ct);
        if (latest == null)
            return (InstallResult.Failed, "⚠ Couldn't reach mkvtoolnix.download — check your connection, or use Page.");

        if (installed != null
            && System.Version.TryParse(installed, out var iv)
            && System.Version.TryParse(latest.Version, out var lv)
            && iv >= lv)
        {
            var msg = $"✓ MKVToolNix {installed} is up to date.";
            progress?.Report((100, msg));
            return (InstallResult.AlreadyCurrent, msg);
        }

        // Newest first; step back only when a version's .7z isn't published yet (404).
        // Never "update" to a version that isn't newer than what's installed.
        var candidates = new List<string> { latest.Version };
        candidates.AddRange(latest.Fallbacks);
        if (installed != null && System.Version.TryParse(installed, out var instV))
            candidates = candidates
                .Where(v => System.Version.TryParse(v, out var cv) && cv > instV)
                .ToList();

        string? lastError = null;
        foreach (var version in candidates)
        {
            var action = installed == null ? "Installing" : $"Updating {installed} →";
            progress?.Report((10, $"{action} MKVToolNix {version}…"));

            var (ok, notFound, err) = await DownloadAndExtractAsync(
                version, SevenZipUrlFor(version), progress, ct);
            if (ok)
            {
                var successMsg = installed == null
                    ? $"✓ MKVToolNix {version} installed."
                    : $"✓ MKVToolNix updated to {version}.";
                progress?.Report((100, successMsg));
                return (installed == null ? InstallResult.Installed : InstallResult.Updated, successMsg);
            }

            lastError = err;
            if (!notFound) break; // a real failure (network, extract) — don't keep trying
        }

        if (candidates.Count == 0)
            return (InstallResult.AlreadyCurrent, $"✓ MKVToolNix {installed} is up to date.");

        return (InstallResult.Failed, $"⚠ Install failed: {lastError ?? "no downloadable build found"}. Use Page.");
    }

    // ── Private ────────────────────────────────────────────────────────────────

    /// <returns>ok; notFound = the .7z returned 404 (caller may try an older version); error text.</returns>
    private static async Task<(bool ok, bool notFound, string? error)> DownloadAndExtractAsync(
        string version, string url, IProgress<(int pct, string status)>? progress, CancellationToken ct)
    {
        // Download the .7z to a temp file (7-Zip needs a real file on disk to extract).
        var tempArchive = Path.Combine(Path.GetTempPath(),
            $"vme-mkvtoolnix-{version}-{Guid.NewGuid():N}.7z");
        try
        {
            progress?.Report((15, $"Downloading MKVToolNix {version}…"));
            using (var resp = await _http.GetAsync(
                url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return (false, true, $"MKVToolNix {version} .7z not found (HTTP 404)");
                if (!resp.IsSuccessStatusCode)
                    return (false, false, $"HTTP {(int)resp.StatusCode} fetching MKVToolNix {version}");

                var total    = resp.Content.Headers.ContentLength ?? 0L;
                var received = 0L;
                await using var http = await resp.Content.ReadAsStreamAsync(ct);
                await using var fs = new FileStream(tempArchive, FileMode.Create, FileAccess.Write,
                    FileShare.None, 65536, FileOptions.Asynchronous);
                var buffer = new byte[65536];
                int read;
                while ((read = await http.ReadAsync(buffer, ct)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                    received += read;
                    if (total > 0)
                        progress?.Report(((int)(15 + received * 55 / total),
                            $"Downloading… {received / 1048576.0:F1}/{total / 1048576.0:F1} MB"));
                }
            }

            // Fresh target folder so stale files from an old version don't linger.
            progress?.Report((72, "Extracting with 7-Zip…"));
            try { if (Directory.Exists(InstallDir)) Directory.Delete(InstallDir, recursive: true); } catch { }
            Directory.CreateDirectory(InstallDir);

            var (exOk, exErr) = await SevenZipService.ExtractAsync(tempArchive, InstallDir, null, ct);
            if (!exOk) return (false, false, $"7-Zip extraction failed: {exErr}");

            // MKVToolNix 7z unpacks into a "mkvtoolnix\" subfolder. Flatten so
            // native\MKVToolNix\mkvpropedit.exe exists (what detection looks for).
            progress?.Report((88, "Finalizing…"));
            FlattenIfNested();

            // Strip Mark-of-the-Web from the extracted exe(s).
            try
            {
                foreach (var exe in Directory.EnumerateFiles(InstallDir, "*.exe", SearchOption.AllDirectories))
                    try { File.Delete(exe + ":Zone.Identifier"); } catch { }
            }
            catch { }

            await Task.Delay(400, ct);

            progress?.Report((95, "Verifying…"));
            MkvPropEditService.Redetect();
            if (!MkvPropEditService.IsAvailable)
                return (false, false,
                    "Extracted to native\\MKVToolNix\\ but mkvpropedit.exe wasn't found afterward. " +
                    "Check the folder, or use Page to install manually.");

            return (true, false, null);
        }
        catch (OperationCanceledException) { return (false, false, "Cancelled."); }
        catch (Exception ex)              { return (false, false, ex.Message); }
        finally
        {
            try { if (File.Exists(tempArchive)) File.Delete(tempArchive); } catch { }
        }
    }

    /// <summary>
    /// The archive extracts to native\MKVToolNix\mkvtoolnix\... — move the inner
    /// folder's contents up one level so mkvpropedit.exe sits directly in InstallDir.
    /// </summary>
    private static void FlattenIfNested()
    {
        try
        {
            if (File.Exists(Path.Combine(InstallDir, "mkvpropedit.exe"))) return; // already flat

            var inner = Path.Combine(InstallDir, "mkvtoolnix");
            if (!Directory.Exists(inner)) return;

            foreach (var file in Directory.EnumerateFiles(inner, "*", SearchOption.AllDirectories))
            {
                var rel  = Path.GetRelativePath(inner, file);
                var dest = Path.Combine(InstallDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Move(file, dest, overwrite: true);
            }
            try { Directory.Delete(inner, recursive: true); } catch { }
        }
        catch { /* leave as-is; detection also checks native\MKVToolNix\mkvpropedit.exe */ }
    }
}
