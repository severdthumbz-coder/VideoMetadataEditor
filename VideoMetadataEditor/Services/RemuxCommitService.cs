using System.IO;

namespace VideoMetadataEditor.Services;

/// <summary>
/// Handles committing or discarding remux candidates created by FfmpegService.
///
/// Naming convention: a remux candidate is "<stem>.remux<ext>" sitting next to the
/// untouched original "<stem><ext>". The original is NEVER renamed — so a crash
/// between remux and commit leaves the library intact plus one obviously-temporary
/// ".remux." file, which the startup orphan sweep can clean up.
///
/// Replace: delete original, rename candidate → original's name (adopt).
/// Restore: delete candidate, keep original (discard).
/// </summary>
public static class RemuxCommitService
{
    /// <summary>Marker inserted before the extension to flag an un-committed remux.</summary>
    public const string Marker = ".remux";

    public record CommitResult(bool Success, string Message, string? FinalPath);

    /// <summary>True if the path looks like an un-committed remux candidate.</summary>
    public static bool IsRemuxCandidate(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        return stem.EndsWith(Marker, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Given a candidate path "Movie.remux.mp4", returns the intended final path
    /// "Movie.mp4" (strips the .remux marker). Keeps the candidate's own extension.
    /// </summary>
    public static string IntendedFinalPath(string candidatePath)
    {
        var dir  = Path.GetDirectoryName(candidatePath) ?? string.Empty;
        var stem = Path.GetFileNameWithoutExtension(candidatePath); // "Movie.remux"
        var ext  = Path.GetExtension(candidatePath);                // ".mp4"
        if (stem.EndsWith(Marker, StringComparison.OrdinalIgnoreCase))
            stem = stem[..^Marker.Length];                          // "Movie"
        return Path.Combine(dir, stem + ext);
    }

    /// <summary>
    /// Replace Original: the candidate becomes the kept file.
    /// Deletes the original (to Recycle Bin), then renames the candidate to the
    /// intended final name. If the final name differs from the original's extension
    /// (e.g. original was .mkv, candidate is .mp4), the original is still removed and
    /// the candidate keeps its own extension.
    /// </summary>
    public static CommitResult ReplaceOriginal(string candidatePath, string originalPath)
    {
        try
        {
            if (!File.Exists(candidatePath))
                return new CommitResult(false, "Remux candidate no longer exists.", null);

            var finalPath = IntendedFinalPath(candidatePath);

            // Remove the original (Recycle Bin, or permanent delete on network paths)
            if (File.Exists(originalPath))
                RecycleOrDelete(originalPath);

            // If the final path is occupied by something other than the candidate, recycle it too
            if (!finalPath.Equals(candidatePath, StringComparison.OrdinalIgnoreCase)
                && File.Exists(finalPath))
                RecycleOrDelete(finalPath);

            File.Move(candidatePath, finalPath);
            return new CommitResult(true,
                $"Original replaced with remuxed copy: {Path.GetFileName(finalPath)}", finalPath);
        }
        catch (Exception ex)
        {
            return new CommitResult(false, $"Replace failed: {ex.Message}", null);
        }
    }

    /// <summary>Sends a file to the Recycle Bin, or permanently deletes on network paths
    /// or when the Recycle Bin is unavailable (headless/CI environments).</summary>
    private static void RecycleOrDelete(string path)
    {
        bool isNetwork = path.StartsWith("\\\\", StringComparison.Ordinal);
        if (!isNetwork)
        {
            try
            {
                var root = Path.GetPathRoot(path);
                if (!string.IsNullOrWhiteSpace(root))
                    isNetwork = new DriveInfo(root).DriveType == DriveType.Network;
            }
            catch { }
        }

        if (!isNetwork)
        {
            try
            {
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                return; // success
            }
            catch
            {
                // Recycle Bin unavailable (headless/CI/network volume) — fall through
            }
        }

        // Permanent delete: network path, or Recycle Bin failed
        File.Delete(path);
    }
    /// Deletes the candidate (Recycle Bin). Original is untouched.
    /// </summary>
    public static CommitResult RestoreOriginal(string candidatePath, string originalPath)
    {
        try
        {
            if (File.Exists(candidatePath))
                RecycleOrDelete(candidatePath);

            var keep = File.Exists(originalPath) ? originalPath : null;
            return new CommitResult(true,
                "Remux discarded — original kept unchanged.", keep);
        }
        catch (Exception ex)
        {
            return new CommitResult(false, $"Restore failed: {ex.Message}", null);
        }
    }

    /// <summary>
    /// Finds un-committed remux candidates (*.remux.*) in a folder, for the
    /// startup orphan sweep. Returns candidate paths.
    /// </summary>
    public static List<string> FindOrphans(string folder, bool recursive)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return result;
        try
        {
            var opt = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            foreach (var f in Directory.EnumerateFiles(folder, "*", opt))
                if (IsRemuxCandidate(f))
                    result.Add(f);
        }
        catch { /* permission / IO — return what we have */ }
        return result;
    }
}
