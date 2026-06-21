using System;
using System.IO;
using VideoMetadataEditor.Services;
using Xunit;

namespace VideoMetadataEditor.Tests;

/// <summary>
/// Tests for MediaHealthService container detection. We synthesise minimal files
/// with the right magic bytes in a temp dir and assert the detected container and
/// the extension-mismatch logic. These are the checks that decide whether a file
/// gets flagged as a problem, so correctness here directly affects user trust.
/// </summary>
public class MediaHealthServiceTests
{
    [Fact]
    public void Analyse_ZeroByteFile_FlaggedAsError()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, "empty.mp4");
            File.WriteAllBytes(path, Array.Empty<byte>());

            var r = MediaHealthService.Analyse(path);

            Assert.Equal(MediaHealthService.HealthStatus.Error, r.Status);
            Assert.Equal(MediaHealthService.IssueType.ZeroBytes, r.Issue);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Analyse_TruncatedFile_FlaggedAsError()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, "tiny.mp4");
            File.WriteAllBytes(path, new byte[] { 0x00, 0x01, 0x02 }); // < 1 KB

            var r = MediaHealthService.Analyse(path);

            Assert.Equal(MediaHealthService.HealthStatus.Error, r.Status);
            Assert.Equal(MediaHealthService.IssueType.ZeroBytes, r.Issue);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Analyse_MatroskaNamedMp4_FlagsExtensionMismatch()
    {
        var dir = CreateTempDir();
        try
        {
            // EBML magic (Matroska) but named .mp4
            var path = Path.Combine(dir, "fake.mp4");
            WriteHeaderPadded(path, new byte[] { 0x1A, 0x45, 0xDF, 0xA3 });

            var r = MediaHealthService.Analyse(path);

            Assert.Equal("Matroska", r.DetectedContainer);
            Assert.Equal(MediaHealthService.IssueType.ExtensionMismatch, r.Issue);
            Assert.Equal(MediaHealthService.HealthStatus.Error, r.Status);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Analyse_CorrectlyNamedMatroska_NoMismatch()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, "real.mkv");
            WriteHeaderPadded(path, new byte[] { 0x1A, 0x45, 0xDF, 0xA3 });

            var r = MediaHealthService.Analyse(path);

            Assert.Equal("Matroska", r.DetectedContainer);
            Assert.NotEqual(MediaHealthService.IssueType.ExtensionMismatch, r.Issue);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Analyse_AviContainer_DetectedCorrectly()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, "clip.avi");
            // RIFF....AVI_ : 'RIFF' + 4 size bytes + 'AVI '
            var header = new byte[]
            {
                0x52, 0x49, 0x46, 0x46, // RIFF
                0x00, 0x00, 0x00, 0x00, // size (ignored)
                0x41, 0x56, 0x49, 0x20  // "AVI "
            };
            WriteHeaderPadded(path, header);

            var r = MediaHealthService.Analyse(path);

            Assert.Equal("AVI", r.DetectedContainer);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Analyse_UnknownContainer_FlaggedAsWarning()
    {
        var dir = CreateTempDir();
        try
        {
            var path = Path.Combine(dir, "mystery.mp4");
            // Random bytes that match no known container signature
            WriteHeaderPadded(path, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE });

            var r = MediaHealthService.Analyse(path);

            Assert.Equal(MediaHealthService.IssueType.UnknownContainer, r.Issue);
            Assert.Equal(MediaHealthService.HealthStatus.Warning, r.Status);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Analyse_MissingFile_FlaggedAsError()
    {
        var r = MediaHealthService.Analyse(@"Z:\nope\missing.mp4");
        Assert.Equal(MediaHealthService.HealthStatus.Error, r.Status);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    /// <summary>Writes the given magic bytes followed by padding to exceed the 1 KB floor.</summary>
    private static void WriteHeaderPadded(string path, byte[] magic)
    {
        var buf = new byte[4096];
        Array.Copy(magic, buf, magic.Length);
        File.WriteAllBytes(path, buf);
    }

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vme_mh_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void CheckEmbeddedComment_SemicolonPresent_FlagsWarning()
    {
        var baseOk = new MediaHealthService.HealthResult(
            @"C:\m.mp4", "m.mp4", ".mp4", "MP4",
            MediaHealthService.HealthStatus.Ok, MediaHealthService.IssueType.None, "", "");
        var flagged = MediaHealthService.CheckEmbeddedComment(baseOk, "A plot; with a semicolon.");
        Assert.Equal(MediaHealthService.HealthStatus.Warning, flagged.Status);
        Assert.Equal(MediaHealthService.IssueType.SemicolonInComment, flagged.Issue);
    }

    [Fact]
    public void CheckEmbeddedComment_NoSemicolon_Unchanged()
    {
        var baseOk = new MediaHealthService.HealthResult(
            @"C:\m.mp4", "m.mp4", ".mp4", "MP4",
            MediaHealthService.HealthStatus.Ok, MediaHealthService.IssueType.None, "", "");
        var result = MediaHealthService.CheckEmbeddedComment(baseOk, "A clean plot, no semicolons.");
        Assert.Equal(MediaHealthService.IssueType.None, result.Issue);
        Assert.Equal(MediaHealthService.HealthStatus.Ok, result.Status);
    }

    [Fact]
    public void CheckEmbeddedComment_DoesNotMaskError()
    {
        var err = new MediaHealthService.HealthResult(
            @"C:\m.mp4", "m.mp4", ".mp4", "—",
            MediaHealthService.HealthStatus.Error, MediaHealthService.IssueType.ZeroBytes,
            "empty", "delete");
        var result = MediaHealthService.CheckEmbeddedComment(err, "has ; semicolon");
        Assert.Equal(MediaHealthService.IssueType.ZeroBytes, result.Issue);   // error preserved
    }

    private static void Cleanup(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best effort */ }
    }
}
