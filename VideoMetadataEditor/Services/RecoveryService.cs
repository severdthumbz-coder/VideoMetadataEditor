using System.IO;
using VideoMetadataEditor.Models;

namespace VideoMetadataEditor.Services;

/// <summary>
/// Scans a set of folders for VME temp/backup orphan files left by a previous
/// crash and produces <see cref="RecoveryCandidate"/> records describing what
/// happened and what action is available.
///
/// Recovery logic:
///   .vme_tmp_<GUID>.<ext>  — crash BEFORE atomic File.Replace.
///     The original file is untouched; temp file has the new tags written to it.
///     Resume = File.Replace(temp, original, null) to complete the swap.
///     Delete = simply remove the temp.
///
///   .vme_bak_<GUID>.<ext>  — crash AFTER File.Replace but before backup cleanup.
///     The replacement already succeeded; backup is redundant.
///     Delete = remove the backup (nothing to resume, the write is already done).
/// </summary>
public static class RecoveryService
{
    private static readonly HashSet<string> VideoExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".mov", ".wmv", ".m4v", ".webm" };

    /// <summary>
    /// Scans <paramref name="folders"/> for orphan VME files.
    /// Returns one <see cref="RecoveryCandidate"/> per orphan found.
    /// </summary>
    public static List<RecoveryCandidate> Scan(IEnumerable<string> folders)
    {
        var results = new List<RecoveryCandidate>();
        var seen    = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var folder in folders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(folder, ".*", SearchOption.AllDirectories))
                {
                    if (seen.Contains(file)) continue;
                    seen.Add(file);

                    var name = Path.GetFileName(file);
                    var ext  = Path.GetExtension(file);
                    var dir  = Path.GetDirectoryName(file) ?? folder;

                    if (name.StartsWith(".vme_tmp_", StringComparison.OrdinalIgnoreCase)
                        && VideoExtensions.Contains(ext))
                    {
                        // Try to identify the original file — look for a video with the
                        // same extension in the same directory that is NOT itself a VME file
                        var candidates = Directory
                            .EnumerateFiles(dir, $"*{ext}")
                            .Where(f => !Path.GetFileName(f).StartsWith(".vme_", StringComparison.OrdinalIgnoreCase))
                            .ToList();

                        // Read the temp file's embedded title to help the user identify it
                        string embeddedTitle = TryReadTitle(file);

                        results.Add(new RecoveryCandidate
                        {
                            TempFilePath       = file,
                            TempFileType       = TempFileType.WriteTemp,
                            PossibleSourceFiles = candidates,
                            EmbeddedTitle      = embeddedTitle,
                            FileSizeBytes      = new FileInfo(file).Length,
                            FolderPath         = dir
                        });
                    }
                    else if (name.StartsWith(".vme_bak_", StringComparison.OrdinalIgnoreCase)
                             && VideoExtensions.Contains(ext))
                    {
                        // Backup after successful write — write already completed.
                        // Only action: delete.
                        results.Add(new RecoveryCandidate
                        {
                            TempFilePath        = file,
                            TempFileType        = TempFileType.WriteBackup,
                            PossibleSourceFiles = new List<string>(),
                            EmbeddedTitle       = TryReadTitle(file),
                            FileSizeBytes       = new FileInfo(file).Length,
                            FolderPath          = dir
                        });
                    }
                }
            }
            catch { /* skip inaccessible folders */ }
        }

        return results;
    }

    /// <summary>
    /// Resumes a failed write by applying the temp file's content to the original.
    /// Only valid for <see cref="TempFileType.WriteTemp"/> candidates.
    /// </summary>
    public static (bool success, string error) Resume(RecoveryCandidate candidate, string targetFilePath)
    {
        try
        {
            if (candidate.TempFileType != TempFileType.WriteTemp)
                return (false, "Resume is only available for incomplete write operations.");
            if (!File.Exists(candidate.TempFilePath))
                return (false, "Temp file no longer exists.");
            if (!File.Exists(targetFilePath))
                return (false, "Target file not found.");

            // Attempt atomic replace first; fall back to copy+delete for cross-volume scenarios
            try
            {
                File.Replace(candidate.TempFilePath, targetFilePath, null, ignoreMetadataErrors: true);
            }
            catch (IOException)
            {
                // File.Replace fails cross-volume — use copy+delete fallback
                File.Copy(candidate.TempFilePath, targetFilePath, overwrite: true);
                File.Delete(candidate.TempFilePath);
            }
            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>Deletes the orphan file.</summary>
    public static (bool success, string error) Delete(RecoveryCandidate candidate)
    {
        try
        {
            if (File.Exists(candidate.TempFilePath))
                File.Delete(candidate.TempFilePath);
            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string TryReadTitle(string path)
    {
        try
        {
            using var tf = TagLib.File.Create(path);
            return tf.Tag?.Title ?? string.Empty;
        }
        catch { return string.Empty; }
    }
}

public enum TempFileType
{
    /// <summary>
    /// Temp copy with new tags written — original is untouched.
    /// Resume = File.Replace to complete the write.
    /// </summary>
    WriteTemp,

    /// <summary>
    /// Backup of original after successful atomic replace.
    /// Write already done. Only action: delete.
    /// </summary>
    WriteBackup
}

public class RecoveryCandidate
{
    public string             TempFilePath        { get; init; } = string.Empty;
    public TempFileType       TempFileType        { get; init; }
    public List<string>       PossibleSourceFiles { get; init; } = new();
    public string             EmbeddedTitle       { get; init; } = string.Empty;
    public long               FileSizeBytes       { get; init; }
    public string             FolderPath          { get; init; } = string.Empty;

    public string FileName       => Path.GetFileName(TempFilePath);
    public string FileSizeDisplay => FileSizeBytes < 1_073_741_824
        ? $"{FileSizeBytes / 1_048_576.0:F1} MB"
        : $"{FileSizeBytes / 1_073_741_824.0:F2} GB";

    /// <summary>
    /// True when the candidate has exactly one possible source file —
    /// i.e. Resume can proceed without user disambiguation.
    /// </summary>
    public bool CanAutoResolveResume => TempFileType == TempFileType.WriteTemp
                                     && PossibleSourceFiles.Count == 1;

    /// <summary>Human-readable description of what happened.</summary>
    public string Description => TempFileType switch
    {
        TempFileType.WriteTemp    => "Incomplete write — new tags were prepared but not applied.",
        TempFileType.WriteBackup  => "Write succeeded — backup was not cleaned up automatically.",
        _ => string.Empty
    };

    public string ActionLabel => TempFileType switch
    {
        TempFileType.WriteTemp   => "Resume (apply tags)",
        TempFileType.WriteBackup => "Delete backup",
        _ => "Delete"
    };
}
