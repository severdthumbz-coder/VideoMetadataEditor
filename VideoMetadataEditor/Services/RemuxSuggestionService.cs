using System.IO;

namespace VideoMetadataEditor.Services;

/// <summary>
/// Decides whether a failed metadata write should prompt the user to remux the file
/// to a clean container. This is the inverse insight from the write self-test: when a
/// write fails for a reason that is NOT about permissions, locks, read-only state, or
/// the volume (i.e. the file is reachable and writable, but TagLib# still couldn't write
/// into the container), the most common real cause is a structurally awkward container
/// that a lossless remux rebuilds cleanly.
///
/// Pure and unit-tested — no UI, no disk, no ffmpeg. The host VM uses the result to
/// decide whether to offer the "remux now?" prompt.
/// </summary>
public static class RemuxSuggestionService
{
    /// <summary>
    /// Container extensions for which a remux suggestion is meaningful. Remuxing targets
    /// MP4/MOV-family containers (the ones whose atom layout TagLib# is fussy about).
    /// Matroska (.mkv) is handled by mkvpropedit and is not part of this suggestion.
    /// </summary>
    private static readonly HashSet<string> RemuxableExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".m4v", ".mov" };

    /// <summary>
    /// Returns true when a remux suggestion is appropriate for this failure:
    ///   • the diagnosis category is container/format-related (FormatUnsupported or
    ///     Unknown) — NOT ReadOnly, FileLocked, PermissionDenied, NetworkVolume, or
    ///     ReadOnlyVolume, which remux would not fix; and
    ///   • the file's extension is one a remux can rebuild (mp4/m4v/mov).
    /// </summary>
    public static bool ShouldSuggestRemux(WriteDiagnosisCategory category, string? filePath)
    {
        if (!IsContainerLevelFailure(category)) return false;
        if (string.IsNullOrWhiteSpace(filePath)) return false;
        var ext = Path.GetExtension(filePath);
        return !string.IsNullOrEmpty(ext) && RemuxableExtensions.Contains(ext);
    }

    /// <summary>
    /// True for failure categories that indicate the write was blocked by the container
    /// itself rather than by access/permission/volume conditions a remux can't fix.
    /// </summary>
    public static bool IsContainerLevelFailure(WriteDiagnosisCategory category)
        => category is WriteDiagnosisCategory.FormatUnsupported
                    or WriteDiagnosisCategory.Unknown;
}
