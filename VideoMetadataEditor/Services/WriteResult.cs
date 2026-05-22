namespace VideoMetadataEditor.Services;

public enum WriteDiagnosisCategory
{
    ReadOnly,
    FileLocked,
    PermissionDenied,
    NetworkVolume,
    ReadOnlyVolume,
    FormatUnsupported,
    Unknown
}

/// <summary>Diagnosis of why a metadata write failed with actionable options.</summary>
public record WriteDiagnosis(
    WriteDiagnosisCategory Category,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<string> Options);

/// <summary>Full result of a WriteMetadataDetailedAsync call.</summary>
public record WriteResult(
    bool Success,
    WriteDiagnosis? Diagnosis,
    IReadOnlyList<string> AttemptErrors)
{
    public static WriteResult Ok() => new(true, null, []);
}
