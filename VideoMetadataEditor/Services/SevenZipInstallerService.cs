using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace VideoMetadataEditor.Services;

/// <summary>
/// Downloads the official 7-Zip standalone console (7zr.exe) into the portable
/// native\ folder, so VME can extract .7z archives (e.g. MKVToolNix) even when
/// the user has no system 7-Zip installed.
///
/// 7zr.exe is a single self-contained executable (~0.5 MB) published at a stable
/// URL — no archive to unpack, so this is a direct download of the .exe itself.
/// Reuses the hardened pattern: in-memory download, atomic temp→replace,
/// Mark-of-the-Web stripping, settle delay, and a post-install sanity run.
/// </summary>
public static class SevenZipInstallerService
{
    private const string ZrDownloadUrl = "https://www.7-zip.org/a/7zr.exe";
    private const string UserAgent =
        "VideoMetadataEditor/1.4 (7zr auto-installer; contact@videometadataeditor.app)";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(60),
        DefaultRequestHeaders = { { "User-Agent", UserAgent } }
    };

    public enum InstallResult { Installed, Updated, AlreadyPresent, Failed }

    /// <summary>Path VME installs 7zr.exe to.</summary>
    public static string NativeZrPath => Path.Combine(NativeLibraryExtractor.NativeDir, "7zr.exe");

    /// <summary>
    /// Downloads 7zr.exe into native\ (overwriting any existing copy) and verifies it runs.
    /// 7-Zip doesn't publish a machine-readable version endpoint for 7zr, so there's no
    /// remote-version check — this always fetches the current published 7zr.exe.
    /// </summary>
    public static async Task<(InstallResult result, string message)> DownloadAsync(
        IProgress<(int pct, string status)>? progress = null, CancellationToken ct = default)
    {
        var existed = File.Exists(NativeZrPath);
        progress?.Report((5, existed ? "Updating 7zr.exe…" : "Downloading 7zr.exe…"));

        try
        {
            using var resp = await _http.GetAsync(
                ZrDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            var total    = resp.Content.Headers.ContentLength ?? 0L;
            var received = 0L;
            using var mem = new MemoryStream(total > 0 ? (int)total : 1024 * 1024);
            await using (var http = await resp.Content.ReadAsStreamAsync(ct))
            {
                var buffer = new byte[65536];
                int read;
                while ((read = await http.ReadAsync(buffer, ct)) > 0)
                {
                    mem.Write(buffer, 0, read);
                    received += read;
                    if (total > 0)
                        progress?.Report(((int)(5 + received * 80 / total),
                            $"Downloading… {received / 1024.0:F0}/{total / 1024.0:F0} KB"));
                }
            }

            Directory.CreateDirectory(NativeLibraryExtractor.NativeDir);
            var tempDest = NativeZrPath + ".tmp";
            await File.WriteAllBytesAsync(tempDest, mem.ToArray(), ct);
            File.Move(tempDest, NativeZrPath, overwrite: true);

            // Strip Mark-of-the-Web so SmartScreen/AppLocker doesn't block execution.
            try { File.Delete(NativeZrPath + ":Zone.Identifier"); } catch { }
            await Task.Delay(300, ct);

            progress?.Report((92, "Verifying 7zr.exe…"));
            if (!await VerifyAsync(NativeZrPath))
                return (InstallResult.Failed,
                    "7zr.exe was downloaded to native\\ but verification failed. " +
                    "Try right-click → Properties → Unblock, or add an antivirus exclusion for native\\.");

            SevenZipService.Redetect();
            var msg = existed ? "✓ 7zr.exe updated." : "✓ 7zr.exe installed.";
            progress?.Report((100, msg));
            return (existed ? InstallResult.Updated : InstallResult.Installed, msg);
        }
        catch (OperationCanceledException) { return (InstallResult.Failed, "Cancelled."); }
        catch (Exception ex)              { return (InstallResult.Failed, ex.Message); }
    }

    private static async Task<bool> VerifyAsync(string path)
    {
        try
        {
            using var proc = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo(path)
                {
                    UseShellExecute = false, RedirectStandardOutput = true,
                    RedirectStandardError = true, CreateNoWindow = true
                }
            };
            proc.Start();
            // 7zr.exe with no args prints its banner then usage; a clean start is enough.
            _ = await proc.StandardOutput.ReadToEndAsync();
            await proc.WaitForExitAsync();
            return true;
        }
        catch { return false; }
    }
}
