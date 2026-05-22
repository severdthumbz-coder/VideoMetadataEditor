using System.IO;
using TagLib;
using File = TagLib.File;

namespace VideoMetadataEditor.Services;

public class VideoMediaInfo
{
    // Video stream
    public string VideoCodec       { get; set; } = string.Empty;
    public int    VideoWidth       { get; set; }
    public int    VideoHeight      { get; set; }
    public double FrameRate        { get; set; }
    public string VideoBitrate     { get; set; } = string.Empty;

    // Audio stream
    public string AudioCodec       { get; set; } = string.Empty;
    public int    AudioChannels    { get; set; }
    public int    SampleRate       { get; set; }
    public int    AudioBitrate     { get; set; }
    public string AudioBitDepth    { get; set; } = string.Empty;

    // Container / duration
    public string ContainerFormat  { get; set; } = string.Empty;
    public TimeSpan Duration       { get; set; }
    public long   FileSizeBytes    { get; set; }

    // ── Formatted display strings ─────────────────────────────────────────────

    public string DurationDisplay =>
        Duration.TotalSeconds > 0
            ? $"{(int)Duration.TotalHours}:{Duration.Minutes:D2}:{Duration.Seconds:D2} ({(int)Duration.TotalSeconds:N0} sec.)"
            : string.Empty;

    public string ResolutionDisplay =>
        VideoWidth > 0 && VideoHeight > 0 ? $"{VideoWidth}×{VideoHeight}" : string.Empty;

    public string VideoLine =>
        string.Join("  ", new[]
        {
            FileSizeBytes > 0 ? FormatSize(FileSizeBytes) : null,
            DurationDisplay,
            string.IsNullOrWhiteSpace(ContainerFormat) ? null : ContainerFormat,
        }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public string VideoCodecLine =>
        string.Join("  ", new[]
        {
            string.IsNullOrWhiteSpace(VideoCodec) ? null : VideoCodec,
            ResolutionDisplay,
            FrameRate > 0 ? $"{FrameRate:F2} fps" : null,
            string.IsNullOrWhiteSpace(VideoBitrate) ? null : VideoBitrate,
        }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public string AudioLine =>
        string.Join("  ", new[]
        {
            string.IsNullOrWhiteSpace(AudioCodec) ? null : AudioCodec,
            string.IsNullOrWhiteSpace(AudioBitDepth) ? null : $"{AudioBitDepth} bit",
            SampleRate > 0 ? $"{SampleRate / 1000.0:G} kHz" : null,
            AudioChannels > 0 ? $"Channels: {AudioChannels}" : null,
            AudioBitrate > 0 ? $"{AudioBitrate} kbps" : null,
        }.Where(s => !string.IsNullOrWhiteSpace(s)));

    public bool HasInfo => VideoWidth > 0 || Duration.TotalSeconds > 0 || AudioBitrate > 0;

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F2} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}

public static class MediaInfoService
{
    public static VideoMediaInfo Read(string filePath)
    {
        var info = new VideoMediaInfo();

        try
        {
            var fileInfo = new FileInfo(filePath);
            info.FileSizeBytes = fileInfo.Length;

            using var tagFile = File.Create(filePath);

            // Duration from TagLib properties
            info.Duration = tagFile.Properties?.Duration ?? TimeSpan.Zero;

            // Container format from MIME type
            info.ContainerFormat = FormatContainer(tagFile.MimeType, filePath);

            var props = tagFile.Properties;
            if (props != null)
            {
                // Video
                info.VideoWidth    = props.VideoWidth;
                info.VideoHeight   = props.VideoHeight;
                info.VideoCodec    = FormatVideoCodec(tagFile.MimeType, filePath);

                // Audio
                info.AudioChannels = props.AudioChannels;
                info.SampleRate    = props.AudioSampleRate;
                info.AudioBitrate  = props.AudioBitrate;
                info.AudioBitDepth = props.BitsPerSample > 0
                    ? props.BitsPerSample.ToString()
                    : string.Empty;
                info.AudioCodec    = FormatAudioCodec(tagFile.MimeType, filePath);
            }
        }
        catch
        {
            // Return whatever we got — partial info is fine
        }

        return info;
    }

    private static string FormatContainer(string? mimeType, string filePath)
    {
        var ext = Path.GetExtension(filePath).TrimStart('.').ToUpperInvariant();
        return (mimeType ?? string.Empty) switch
        {
            var m when m.Contains("mp4") || m.Contains("mpeg4") => $"isom (ISO 14496-1 Base Media)",
            var m when m.Contains("x-matroska")                  => "Matroska",
            var m when m.Contains("quicktime")                    => "QuickTime",
            var m when m.Contains("webm")                         => "WebM",
            var m when m.Contains("ms-wmv") || m.Contains("wmv") => "Windows Media",
            _ => ext
        };
    }

    private static string FormatVideoCodec(string? mimeType, string filePath)
    {
        var ext = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "mp4" or "m4v" => "MPEG-4",
            "mkv"          => "H.264 / AVC",
            "mov"          => "H.264 / AVC",
            "wmv"          => "VC-1",
            "webm"         => "VP9",
            _ => string.Empty
        };
    }

    private static string FormatAudioCodec(string? mimeType, string filePath)
    {
        var ext = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "mp4" or "m4v" => "MPEG-4 AAC LC",
            "mkv"          => "AAC",
            "mov"          => "AAC",
            "wmv"          => "WMA",
            "webm"         => "Opus",
            _ => string.Empty
        };
    }
}
