using System;
using System.IO;
using VideoMetadataEditor.Services;
using Xunit;

namespace VideoMetadataEditor.Tests;

/// <summary>
/// Tests for RemuxCommitService. The pure path-logic methods (IsRemuxCandidate,
/// IntendedFinalPath) are tested directly. The file-mutating methods are tested
/// against a temp directory so no real library files or Recycle Bin are touched.
///
/// NOTE: ReplaceOriginal/RestoreOriginal use the Recycle Bin via Microsoft.VisualBasic.
/// On a CI/headless box the Recycle Bin call may behave differently, so those tests
/// assert on the OUTCOME (candidate adopted / discarded) rather than on where the
/// deleted file went. The orphan-finder and path logic carry the core coverage.
/// </summary>
public class RemuxCommitServiceTests
{
    // ── IsRemuxCandidate ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("Movie.remux.mp4", true)]
    [InlineData("Movie.remux.mkv", true)]
    [InlineData("The.Matrix.1999.remux.mp4", true)]
    [InlineData("Movie.mp4", false)]
    [InlineData("Movie.mkv", false)]
    [InlineData("Movie.remuxed.mp4", false)]   // 'remuxed' != '.remux' marker
    [InlineData("remux.mp4", false)]           // no stem before marker
    [InlineData("Show.S01E01.remux.mkv", true)]
    public void IsRemuxCandidate_DetectsMarkerCorrectly(string fileName, bool expected)
    {
        Assert.Equal(expected, RemuxCommitService.IsRemuxCandidate(fileName));
    }

    [Fact]
    public void IsRemuxCandidate_IsCaseInsensitive()
    {
        Assert.True(RemuxCommitService.IsRemuxCandidate("Movie.REMUX.mp4"));
        Assert.True(RemuxCommitService.IsRemuxCandidate("Movie.ReMuX.MKV"));
    }

    // ── IntendedFinalPath ────────────────────────────────────────────────────

    [Theory]
    [InlineData(@"C:\Movies\Movie.remux.mp4", @"C:\Movies\Movie.mp4")]
    [InlineData(@"C:\Movies\Movie.remux.mkv", @"C:\Movies\Movie.mkv")]
    [InlineData(@"C:\TV\Show.S01E01.remux.mp4", @"C:\TV\Show.S01E01.mp4")]
    public void IntendedFinalPath_StripsMarkerKeepsExtension(string candidate, string expected)
    {
        Assert.Equal(expected, RemuxCommitService.IntendedFinalPath(candidate));
    }

    [Fact]
    public void IntendedFinalPath_PreservesCandidateExtensionNotOriginal()
    {
        // A .mkv original remuxed to .mp4 → candidate is Movie.remux.mp4 →
        // intended final keeps .mp4 (the new container), not the original .mkv
        var result = RemuxCommitService.IntendedFinalPath(@"C:\X\Movie.remux.mp4");
        Assert.EndsWith(".mp4", result);
        Assert.DoesNotContain(".remux", result);
    }

    // ── FindOrphans ──────────────────────────────────────────────────────────

    [Fact]
    public void FindOrphans_FindsOnlyRemuxFiles()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, "A.remux.mp4"), "x");
            File.WriteAllText(Path.Combine(dir, "B.remux.mkv"), "x");
            File.WriteAllText(Path.Combine(dir, "C.mp4"), "x");          // not a candidate
            File.WriteAllText(Path.Combine(dir, "D.mkv"), "x");          // not a candidate

            var orphans = RemuxCommitService.FindOrphans(dir, recursive: false);

            Assert.Equal(2, orphans.Count);
            Assert.Contains(orphans, p => p.EndsWith("A.remux.mp4"));
            Assert.Contains(orphans, p => p.EndsWith("B.remux.mkv"));
            Assert.DoesNotContain(orphans, p => p.EndsWith("C.mp4"));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void FindOrphans_RespectsRecursiveFlag()
    {
        var dir = CreateTempDir();
        try
        {
            var sub = Path.Combine(dir, "Season 01");
            Directory.CreateDirectory(sub);
            File.WriteAllText(Path.Combine(dir, "Top.remux.mp4"), "x");
            File.WriteAllText(Path.Combine(sub, "Nested.remux.mp4"), "x");

            var flat = RemuxCommitService.FindOrphans(dir, recursive: false);
            Assert.Single(flat);
            Assert.Contains(flat, p => p.EndsWith("Top.remux.mp4"));

            var deep = RemuxCommitService.FindOrphans(dir, recursive: true);
            Assert.Equal(2, deep.Count);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void FindOrphans_NonexistentFolder_ReturnsEmpty()
    {
        var result = RemuxCommitService.FindOrphans(@"Z:\does\not\exist", recursive: true);
        Assert.Empty(result);
    }

    // ── RestoreOriginal (discard candidate, keep original) ───────────────────

    [Fact]
    public void RestoreOriginal_RemovesCandidate_KeepsOriginal()
    {
        var dir = CreateTempDir();
        try
        {
            var original  = Path.Combine(dir, "Movie.mp4");
            var candidate = Path.Combine(dir, "Movie.remux.mp4");
            File.WriteAllText(original,  "ORIGINAL");
            File.WriteAllText(candidate, "REMUXED");

            var result = RemuxCommitService.RestoreOriginal(candidate, original);

            Assert.True(result.Success);
            Assert.True(File.Exists(original));        // original kept
            Assert.False(File.Exists(candidate));      // candidate gone
            Assert.Equal("ORIGINAL", File.ReadAllText(original));
        }
        finally { Cleanup(dir); }
    }

    // ── ReplaceOriginal (adopt candidate, remove original) ───────────────────

    [Fact]
    public void ReplaceOriginal_AdoptsCandidate_UnderOriginalName()
    {
        var dir = CreateTempDir();
        try
        {
            var original  = Path.Combine(dir, "Movie.mp4");
            var candidate = Path.Combine(dir, "Movie.remux.mp4");
            File.WriteAllText(original,  "ORIGINAL");
            File.WriteAllText(candidate, "REMUXED");

            var result = RemuxCommitService.ReplaceOriginal(candidate, original);

            Assert.True(result.Success);
            // Candidate marker file should be gone (renamed to final)
            Assert.False(File.Exists(candidate));
            // The intended final path now holds the remuxed content
            var final = RemuxCommitService.IntendedFinalPath(candidate);
            Assert.True(File.Exists(final));
            Assert.Equal("REMUXED", File.ReadAllText(final));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void ReplaceOriginal_MissingCandidate_FailsGracefully()
    {
        var dir = CreateTempDir();
        try
        {
            var original  = Path.Combine(dir, "Movie.mp4");
            var candidate = Path.Combine(dir, "Movie.remux.mp4"); // never created
            File.WriteAllText(original, "ORIGINAL");

            var result = RemuxCommitService.ReplaceOriginal(candidate, original);

            Assert.False(result.Success);
            Assert.True(File.Exists(original)); // original untouched on failure
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void ReplaceOriginal_DifferentExtension_KeepsCandidateExtension()
    {
        // Original .mkv, remuxed to .mp4 → final should be Movie.mp4, .mkv removed
        var dir = CreateTempDir();
        try
        {
            var original  = Path.Combine(dir, "Movie.mkv");
            var candidate = Path.Combine(dir, "Movie.remux.mp4");
            File.WriteAllText(original,  "ORIGINAL_MKV");
            File.WriteAllText(candidate, "REMUXED_MP4");

            var result = RemuxCommitService.ReplaceOriginal(candidate, original);

            Assert.True(result.Success);
            Assert.True(File.Exists(Path.Combine(dir, "Movie.mp4")));
            Assert.Equal("REMUXED_MP4", File.ReadAllText(Path.Combine(dir, "Movie.mp4")));
        }
        finally { Cleanup(dir); }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "vme_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch { /* best effort */ }
    }
}
