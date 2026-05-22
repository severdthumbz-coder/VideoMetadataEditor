using System.IO;
using System.Reflection;

namespace VideoMetadataEditor.Services;

/// <summary>
/// Extracts embedded native binaries to a sibling "native" folder on first run.
///
/// Currently manages:
///   fpcalc.exe — Chromaprint's standalone fingerprinting tool.
///     Statically links FFmpeg so it decodes any audio format with no
///     additional dependencies. The user places fpcalc.exe in the Native/
///     source folder before building; it is embedded as an assembly resource
///     and extracted to AppDir\native\ automatically on first run.
///
/// Portable: extraction happens to the EXE's own directory so the app can
/// run from any location including a USB drive.
/// </summary>
public static class NativeLibraryExtractor
{
    private static bool _initialised;
    private static readonly object _lock = new();

    public static string NativeDir { get; } = Path.Combine(
        Path.GetDirectoryName(Environment.ProcessPath)
            ?? AppContext.BaseDirectory,
        "native");

    /// <summary>
    /// Called once from App.xaml.cs at startup.
    /// Extracts fpcalc.exe from the assembly resource to NativeDir.
    /// </summary>
    public static void EnsureExtracted()
    {
        lock (_lock)
        {
            if (_initialised) return;
            _initialised = true;

            Directory.CreateDirectory(NativeDir);

            ExtractEmbedded(
                "VideoMetadataEditor.Native.fpcalc.exe",
                "fpcalc.exe");
        }
    }

    public static bool IsFpcalcAvailable()
        => File.Exists(Path.Combine(NativeDir, "fpcalc.exe"));

    // ── Private ───────────────────────────────────────────────────────────────

    private static void ExtractEmbedded(string resourceName, string outputName)
    {
        var dest = Path.Combine(NativeDir, outputName);

        // Always check the embedded version is current — compare sizes
        var asm    = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(resourceName);
        if (stream == null) return; // not embedded — user must place manually

        // Only re-extract if missing or different size (version update)
        if (File.Exists(dest))
        {
            var existing = new FileInfo(dest);
            if (existing.Length == stream.Length) return;
        }

        using var fs = new FileStream(dest, FileMode.Create, FileAccess.Write,
            FileShare.None, 65536, FileOptions.WriteThrough);
        stream.CopyTo(fs);

        System.Diagnostics.Debug.WriteLine(
            $"[NativeLibraryExtractor] Extracted {outputName} → {dest}");
    }
}
