using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace VideoMetadataEditor.Services;

/// <summary>
/// Resolves a 7-Zip-capable command-line extractor and uses it to unpack .7z
/// archives — which .NET's built-in ZipArchive cannot read. Needed for tools
/// distributed only as .7z (e.g. MKVToolNix).
///
/// Resolution order (hybrid, per the user's choice):
///   1. native\7zr.exe            — the standalone console build VME can fetch itself
///   2. system 7z.exe / 7za.exe   — an existing 7-Zip install (ProgramFiles, registry, PATH)
/// If neither is present, callers fall back to opening the tool's download page.
/// </summary>
public static class SevenZipService
{
    public enum SourceKind { None, Native, System }

    private static string? _exePath;
    private static SourceKind _source = SourceKind.None;
    private static bool _detected;

    public static bool IsAvailable => Resolve() != null;
    public static string ExePath   => Resolve() ?? string.Empty;
    public static SourceKind Source { get { Resolve(); return _source; } }

    /// <summary>Re-run detection (after installing 7zr.exe or a system 7-Zip).</summary>
    public static void Redetect() { _detected = false; Resolve(); }

    /// <summary>Resolves the first available 7z extractor, caching the result.</summary>
    public static string? Resolve(bool force = false)
    {
        if (_detected && !force) return _exePath;
        _detected = true;
        _exePath  = null;
        _source   = SourceKind.None;

        // 1) VME's own native\7zr.exe
        var nativeZr = Path.Combine(NativeLibraryExtractor.NativeDir, "7zr.exe");
        if (File.Exists(nativeZr)) { _exePath = nativeZr; _source = SourceKind.Native; return _exePath; }

        // 2) System 7-Zip — common install dirs
        foreach (var candidate in SystemCandidates())
        {
            if (File.Exists(candidate)) { _exePath = candidate; _source = SourceKind.System; return _exePath; }
        }

        // 3) Registry: HKLM\SOFTWARE\7-Zip\Path
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\7-Zip");
            var dir = key?.GetValue("Path") as string;
            if (!string.IsNullOrWhiteSpace(dir))
            {
                foreach (var exe in new[] { "7z.exe", "7za.exe" })
                {
                    var p = Path.Combine(dir, exe);
                    if (File.Exists(p)) { _exePath = p; _source = SourceKind.System; return _exePath; }
                }
            }
        }
        catch { /* registry not readable — ignore */ }

        // 4) PATH
        foreach (var exe in new[] { "7z.exe", "7za.exe" })
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("where", exe)
                {
                    UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true
                });
                var outp = p!.StandardOutput.ReadLine();
                p.WaitForExit();
                if (!string.IsNullOrWhiteSpace(outp) && File.Exists(outp.Trim()))
                { _exePath = outp.Trim(); _source = SourceKind.System; return _exePath; }
            }
            catch { /* not on PATH */ }
        }

        return _exePath;
    }

    private static System.Collections.Generic.IEnumerable<string> SystemCandidates()
    {
        var pf   = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        foreach (var root in new[] { pf, pf86 })
        {
            if (string.IsNullOrEmpty(root)) continue;
            yield return Path.Combine(root, "7-Zip", "7z.exe");
            yield return Path.Combine(root, "7-Zip", "7za.exe");
        }
    }

    /// <summary>
    /// Extracts <paramref name="archivePath"/> into <paramref name="destDir"/>.
    /// Uses "x" (full paths) with overwrite. Optionally restrict to a filter
    /// (e.g. "mkvpropedit.exe") so only the wanted file is written.
    /// Returns (true, null) on success.
    /// </summary>
    public static async Task<(bool ok, string? error)> ExtractAsync(
        string archivePath, string destDir, string? fileFilter = null, CancellationToken ct = default)
    {
        var exe = Resolve();
        if (exe == null) return (false, "No 7-Zip extractor available.");

        try
        {
            Directory.CreateDirectory(destDir);
            // x = extract with full paths; -y = assume yes; -o = output dir (no space).
            // A trailing filter limits extraction to matching entries (recursive via -r).
            var args = $"x \"{archivePath}\" -o\"{destDir}\" -y -r";
            if (!string.IsNullOrWhiteSpace(fileFilter))
                args += $" \"{fileFilter}\"";

            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true,
                }
            };
            proc.Start();
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync(ct);

            if (proc.ExitCode != 0)
                return (false, $"7-Zip exit {proc.ExitCode}: " +
                    (!string.IsNullOrWhiteSpace(stderr) ? stderr.Trim() : stdout.Trim()));

            return (true, null);
        }
        catch (OperationCanceledException) { return (false, "Cancelled."); }
        catch (Exception ex)              { return (false, ex.Message); }
    }
}
