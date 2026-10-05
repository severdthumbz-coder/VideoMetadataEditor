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
    private const string LatestXmlUrl = "https://mkvtoolnix.download/latest-release.xml";
    private const string UserAgent =
        "VideoMetadataEditor/1.4 (mkvtoolnix auto-updater; contact@videometadataeditor.app)";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(3),
        DefaultRequestHeaders = { { "User-Agent", UserAgent } }
    };

    public enum InstallResult { AlreadyCurrent, Installed, Updated, Failed, NoExtractor }

    public record ReleaseInfo(string Version, string DownloadUrl);

    /// <summary>Where the portable MKVToolNix folder is installed.</summary>
    public static string InstallDir =>
        Path.Combine(NativeLibraryExtractor.NativeDir, "MKVToolNix");

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the latest release version from mkvtoolnix.download and builds the
    /// portable 64-bit 7z URL from it. Returns null on network/parse error.
    /// </summary>
    public static async Task<ReleaseInfo?> GetLatestReleaseAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await _http.GetAsync(LatestXmlUrl, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var xml = await resp.Content.ReadAsStringAsync(ct);

            // Parse defensively: grab the first N.N(.N) version token in the feed.
            var m = Regex.Match(xml, @"(\d+\.\d+(?:\.\d+)?)");
            if (!m.Success) return null;
            var version = m.Value;

            // Stable portable path convention.
            var url = $"https://mkvtoolnix.download/windows/releases/{version}/mkvtoolnix-64-bit-{version}.7z";
            return new ReleaseInfo(version, url);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MkvToolNixInstaller] error: {ex.Message}");
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

        var action = installed == null ? "Installing" : $"Updating {installed} →";
        progress?.Report((10, $"{action} MKVToolNix {latest.Version}…"));

        var (ok, err) = await DownloadAndExtractAsync(latest, progress, ct);
        if (!ok)
            return (InstallResult.Failed, $"⚠ Install failed: {err}");

        var successMsg = installed == null
            ? $"✓ MKVToolNix {latest.Version} installed."
            : $"✓ MKVToolNix updated to {latest.Version}.";
        progress?.Report((100, successMsg));
        return (installed == null ? InstallResult.Installed : InstallResult.Updated, successMsg);
    }

    // ── Private ────────────────────────────────────────────────────────────────

    private static async Task<(bool ok, string? error)> DownloadAndExtractAsync(
        ReleaseInfo release, IProgress<(int pct, string status)>? progress, CancellationToken ct)
    {
        // Download the .7z to a temp file (7-Zip needs a real file on disk to extract).
        var tempArchive = Path.Combine(Path.GetTempPath(),
            $"vme-mkvtoolnix-{release.Version}-{Guid.NewGuid():N}.7z");
        try
        {
            progress?.Report((15, $"Downloading MKVToolNix {release.Version}…"));
            using (var resp = await _http.GetAsync(
                release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (!resp.IsSuccessStatusCode)
                    return (false, $"HTTP {(int)resp.StatusCode} fetching the 7z (the version URL may have moved). Use Page.");

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
            if (!exOk) return (false, $"7-Zip extraction failed: {exErr}");

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
                return (false,
                    "Extracted to native\\MKVToolNix\\ but mkvpropedit.exe wasn't found afterward. " +
                    "Check the folder, or use Page to install manually.");

            return (true, null);
        }
        catch (OperationCanceledException) { return (false, "Cancelled."); }
        catch (Exception ex)              { return (false, ex.Message); }
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
