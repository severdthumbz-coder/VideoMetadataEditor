namespace VideoMetadataEditor.Models;

public enum SubtitleFormat
{
    SRT,   // SubRip
    ASS,   // Advanced SubStation Alpha
    SSA,   // SubStation Alpha
    VTT,   // WebVTT
    SUB,   // MicroDVD / VobSub
    IDX,   // VobSub index
    SBV,   // YouTube
    LRC,   // Lyrics
    Unknown
}

/// <summary>A subtitle sidecar file detected alongside a video file.</summary>
public class SubtitleFile
{
    public string FilePath      { get; set; } = string.Empty;
    public string FileName      => System.IO.Path.GetFileName(FilePath);
    public SubtitleFormat Format { get; set; }
    public string FormatDisplay => Format.ToString().ToUpperInvariant();

    // Language parsed from filename: Movie.en.srt → "en"
    public string LanguageCode    { get; set; } = string.Empty;
    public string LanguageDisplay { get; set; } = string.Empty;

    // Flags parsed from filename: Movie.en.forced.srt, Movie.en.hi.srt
    public bool IsForced           { get; set; }
    public bool IsHearingImpaired  { get; set; }  // .hi suffix

    public long FileSizeBytes { get; set; }

    public string FlagDisplay
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(LanguageDisplay)) parts.Add(LanguageDisplay);
            if (IsForced)          parts.Add("Forced");
            if (IsHearingImpaired) parts.Add("HI");
            return parts.Count > 0 ? string.Join(", ", parts) : "Unknown";
        }
    }
}
