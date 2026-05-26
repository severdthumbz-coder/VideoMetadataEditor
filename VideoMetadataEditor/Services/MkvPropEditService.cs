using System.Diagnostics;
using System.IO;

namespace VideoMetadataEditor.Services;

/// <summary>
/// Thin wrapper around mkvpropedit (part of MKVToolNix) for writing artwork
/// to Matroska (.mkv) files reliably.
///
/// TagLib# stores MKV cover art as a generic binary attachment — many media
/// servers (Plex, Jellyfin, Emby) ignore it or display it inconsistently.
/// MKVPropEdit writes a proper Matroska attachment named "cover.jpg" / "cover.png"
/// which all major players recognise as front cover art.
///
/// Falls back gracefully to TagLib# if MKVPropEdit is not installed.
/// No UI change required — behaviour is transparent to the user.
/// </summary>
public static class MkvPropEditService
{
    private static string? _exePath;
    private static string? _version;

    // ── Detection ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Searches standard MKVToolNix install locations, the app's native\ folder,
    /// and PATH. Called once at startup; call Redetect() if the user installs
    /// MKVToolNix after launch.
    /// </summary>
    public static void Detect()
    {
        var nativeDir = Services.NativeLibraryExtractor.NativeDir;

        var candidates = new List<string>
        {
            // ── Portable / native folder (highest priority) ──────────────────
            // User drops mkvpropedit.exe directly into the native\ folder
            Path.Combine(nativeDir, "mkvpropedit.exe"),
            // User drops the full MKVToolNix folder into native\
            Path.Combine(nativeDir, "MKVToolNix", "mkvpropedit.exe"),

            // ── App folder variants ──────────────────────────────────────────
            Path.Combine(AppContext.BaseDirectory, "mkvpropedit.exe"),
            Path.Combine(AppContext.BaseDirectory, "MKVToolNix", "mkvpropedit.exe"),
            Path.Combine(AppContext.BaseDirectory, "mkvtoolnix", "mkvpropedit.exe"),

            // ── Standard Windows installs ────────────────────────────────────
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "MKVToolNix", "mkvpropedit.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "MKVToolNix", "mkvpropedit.exe"),

            // ── Package managers ─────────────────────────────────────────────
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "scoop", "apps", "mkvtoolnix", "current", "mkvpropedit.exe"),
        };

        _exePath = candidates.FirstOrDefault(File.Exists);
        _version = null;

        if (_exePath == null)
        {
            // Try PATH last
            try
            {
                using var proc = Process.Start(new ProcessStartInfo("mkvpropedit", "--version")
                {
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true
                });
                proc?.WaitForExit(2000);
                if (proc?.ExitCode == 0)
                    _exePath = "mkvpropedit"; // on PATH
            }
            catch { /* not on PATH */ }
        }

        // Cache the version string for the Settings status display
        if (_exePath != null)
        {
            try
            {
                using var proc = Process.Start(new ProcessStartInfo(_exePath, "--version")
                {
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true
                });
                proc?.WaitForExit(2000);
                _version = proc?.StandardOutput.ReadToEnd()
                    .Split('\n').FirstOrDefault(l => l.StartsWith("mkvpropedit"))
                    ?.Trim() ?? string.Empty;
            }
            catch { _version = string.Empty; }
        }
    }

    /// <summary>
    /// Re-runs detection — call when the user installs MKVToolNix after launch
    /// or drops mkvpropedit.exe into the native\ folder.
    /// </summary>
    public static void Redetect()
    {
        _exePath = null;
        _version = null;
        Detect();
    }

    /// <summary>True if mkvpropedit was found during Detect().</summary>
    public static bool IsAvailable => _exePath != null;

    /// <summary>Full path or command name of mkvpropedit.</summary>
    public static string ExePath => _exePath ?? string.Empty;

    /// <summary>Version string reported by mkvpropedit --version, or empty if not found.</summary>
    public static string Version => _version ?? string.Empty;

    // ── Artwork write ─────────────────────────────────────────────────────────

    /// <summary>
    /// Embeds artwork into an MKV file using mkvpropedit.
    /// Adds or replaces the attachment named "cover.jpg" / "cover.png".
    ///
    /// Strategy:
    ///   1. Delete any existing attachment named cover.jpg or cover.png.
    ///   2. Add the new artwork as cover.jpg (JPEG) or cover.png (PNG).
    ///
    /// Returns (true, null) on success; (false, errorMessage) on failure.
    /// The caller should fall back to TagLib# on failure.
    /// </summary>
    public static async Task<(bool success, string? error)> SetArtworkAsync(
        string mkvPath,
        byte[] artworkBytes,
        CancellationToken ct = default)
    {
        if (!IsAvailable)
            return (false, "mkvpropedit not found — falling back to TagLib#.");

        if (!File.Exists(mkvPath))
            return (false, $"File not found: {mkvPath}");

        if (artworkBytes is not { Length: > 0 })
            return (false, "No artwork bytes provided.");

        // Detect JPEG vs PNG from magic bytes
        bool isJpeg = artworkBytes.Length >= 2
            && artworkBytes[0] == 0xFF && artworkBytes[1] == 0xD8;
        var mimeType   = isJpeg ? "image/jpeg" : "image/png";
        var coverName  = isJpeg ? "cover.jpg"  : "cover.png";
        var deleteNames = isJpeg
            ? new[] { "cover.jpg", "cover.jpeg", "COVER.JPG" }
            : new[] { "cover.png", "COVER.PNG" };

        // Write artwork bytes to a temp file
        var tempDir   = Path.GetDirectoryName(mkvPath) ?? Path.GetTempPath();
        var tempArt   = Path.Combine(tempDir, $".vme_art_{Guid.NewGuid():N}.{(isJpeg ? "jpg" : "png")}");

        try
        {
            await File.WriteAllBytesAsync(tempArt, artworkBytes, ct);

            // Build mkvpropedit arguments:
            //   --delete-attachment name:cover.jpg  (remove existing — ignore errors)
            //   --attachment-name cover.jpg
            //   --attachment-mime-type image/jpeg
            //   --add-attachment /path/to/tempArt
            var args = BuildArgs(mkvPath, tempArt, coverName, mimeType, deleteNames);

            var (exitCode, stdout, stderr) = await RunAsync(_exePath!, args, ct);

            if (exitCode == 0)
                return (true, null);

            // Exit code 1 = warnings (still succeeded), exit code 2 = error
            if (exitCode == 1)
                return (true, $"mkvpropedit warnings: {stderr}");

            return (false,
                $"mkvpropedit exited {exitCode}: {stderr}{(string.IsNullOrWhiteSpace(stdout) ? "" : $" | {stdout}")}");
        }
        finally
        {
            try { if (File.Exists(tempArt)) File.Delete(tempArt); }
            catch { /* non-fatal */ }
        }
    }

    /// <summary>
    /// Reads current attachments from an MKV file to check whether cover art
    /// is already embedded (for display in the preview panel).
    /// Returns the first attachment named cover.* as raw bytes, or null.
    /// </summary>
    public static async Task<byte[]?> ReadArtworkAsync(string mkvPath, CancellationToken ct = default)
    {
        // mkvpropedit can't read — use mkvmerge --identify for listing only.
        // For reading actual bytes we still use TagLib# (it reads attachments fine).
        // This method is a hook for future mkvextract integration.
        return await Task.FromResult<byte[]?>(null);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string BuildArgs(
        string mkvPath, string artPath,
        string coverName, string mimeType,
        string[] deleteNames)
    {
        var sb = new System.Text.StringBuilder();

        // Quoted paths
        sb.Append($"\"{mkvPath}\"");

        // Delete existing cover attachments (non-fatal if they don't exist)
        foreach (var n in deleteNames)
            sb.Append($" --delete-attachment name:{n}");

        // Add new attachment
        sb.Append($" --attachment-name \"{coverName}\"");
        sb.Append($" --attachment-mime-type \"{mimeType}\"");
        sb.Append($" --attachment-description \"Front cover\"");
        sb.Append($" --add-attachment \"{artPath}\"");

        return sb.ToString();
    }

    private static async Task<(int exitCode, string stdout, string stderr)> RunAsync(
        string exe, string args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);

        await proc.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        return (proc.ExitCode, stdout.Trim(), stderr.Trim());
    }
}
