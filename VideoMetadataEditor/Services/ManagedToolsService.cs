using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VideoMetadataEditor.Services;

/// <summary>
/// A uniform facade over the external command-line tools VME relies on
/// (ffmpeg, mkvpropedit, fpcalc). Each concrete tool wires this up to the
/// existing per-tool service (FfmpegService, MkvPropEditService,
/// FpcalcInstallerService) rather than reimplementing detection or download —
/// so the proven logic stays put and the UI binds to one consistent shape.
///
/// All tools live under &lt;AppDir&gt;\native\ (see NativeLibraryExtractor.NativeDir),
/// which is created at startup and can be recreated on demand.
/// </summary>
public sealed class ManagedTool
{
    public required string Key          { get; init; }   // stable id, e.g. "ffmpeg"
    public required string DisplayName  { get; init; }   // e.g. "FFmpeg"
    public required string Purpose      { get; init; }   // one line: why VME uses it
    public required string DownloadPageUrl { get; init; } // browser fallback

    /// <summary>True when this tool can fetch + install itself in-app.
    /// False means the only action is "open the download page".</summary>
    public bool CanAutoDownload { get; init; }

    // Delegates into the concrete service. Kept as funcs so the static
    // services don't need a shared base class or interface.
    public required System.Func<bool>   IsInstalled      { get; init; }
    public required System.Func<string> ExePath          { get; init; }
    public required System.Func<Task<string?>> GetInstalledVersionAsync { get; init; }
    public System.Func<CancellationToken, Task<string?>>? GetLatestVersionAsync { get; init; }
    public System.Func<System.IProgress<(int pct, string msg)>?, CancellationToken, Task<(bool ok, string message)>>? InstallOrUpdateAsync { get; init; }
    public System.Action? Redetect { get; init; }
}

/// <summary>
/// Registry of the external tools, plus native-folder helpers. Static so the
/// VM and startup code share one list.
/// </summary>
public static class ManagedToolsService
{
    public static string NativeDir => NativeLibraryExtractor.NativeDir;

    public static bool NativeFolderExists => Directory.Exists(NativeDir);

    /// <summary>Creates the native\ folder if it doesn't exist. Returns the path.</summary>
    public static string EnsureNativeFolder()
    {
        Directory.CreateDirectory(NativeDir);
        return NativeDir;
    }

    private static List<ManagedTool>? _tools;

    public static IReadOnlyList<ManagedTool> Tools => _tools ??= Build();

    private static List<ManagedTool> Build() => new()
    {
        // ── FFmpeg ──────────────────────────────────────────────────────────
        new ManagedTool
        {
            Key             = "ffmpeg",
            DisplayName     = "FFmpeg",
            Purpose         = "Lossless remux / faststart fixes (Health Check) and audio extraction.",
            DownloadPageUrl = "https://ffmpeg.org/download.html",
            CanAutoDownload = false, // real download added in a later build
            IsInstalled     = () => FfmpegService.IsAvailable,
            ExePath         = () => FfmpegService.ExePath ?? string.Empty,
            GetInstalledVersionAsync = () => FfmpegService.GetVersionAsync(),
            Redetect        = () => FfmpegService.Detect(forceRedetect: true),
        },

        // ── MKVToolNix (mkvpropedit) ────────────────────────────────────────
        new ManagedTool
        {
            Key             = "mkvpropedit",
            DisplayName     = "MKVToolNix (mkvpropedit)",
            Purpose         = "Proper Matroska cover.jpg embedding for MKV artwork (Plex/Jellyfin).",
            DownloadPageUrl = "https://mkvtoolnix.download/downloads.html",
            CanAutoDownload = false, // real download added in a later build
            IsInstalled     = () => MkvPropEditService.IsAvailable,
            ExePath         = () => MkvPropEditService.ExePath,
            GetInstalledVersionAsync = () => Task.FromResult<string?>(
                string.IsNullOrWhiteSpace(MkvPropEditService.Version) ? null : MkvPropEditService.Version),
            Redetect        = () => MkvPropEditService.Redetect(),
        },

        // ── fpcalc (Chromaprint) ────────────────────────────────────────────
        new ManagedTool
        {
            Key             = "fpcalc",
            DisplayName     = "fpcalc (Chromaprint)",
            Purpose         = "Audio fingerprinting for content-based duplicate detection.",
            DownloadPageUrl = "https://github.com/acoustid/chromaprint/releases/latest",
            CanAutoDownload = true, // FpcalcInstallerService already implements this
            IsInstalled     = () => File.Exists(Path.Combine(NativeDir, "fpcalc.exe")),
            ExePath         = () => Path.Combine(NativeDir, "fpcalc.exe"),
            GetInstalledVersionAsync = () => FpcalcInstallerService.GetInstalledVersionAsync(),
            GetLatestVersionAsync = async ct =>
            {
                var r = await FpcalcInstallerService.GetLatestReleaseAsync(ct);
                return r?.Version;
            },
            InstallOrUpdateAsync = async (progress, ct) =>
            {
                var (result, message) = await FpcalcInstallerService.CheckAndInstallAsync(progress, ct);
                var ok = result is FpcalcInstallerService.InstallResult.Installed
                              or FpcalcInstallerService.InstallResult.Updated
                              or FpcalcInstallerService.InstallResult.AlreadyCurrent;
                return (ok, message);
            },
            Redetect = null, // fpcalc is resolved by file existence; nothing to re-probe
        },
    };
}
