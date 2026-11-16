using System;
using System.Net.Http;
using System.Threading;
using VideoMetadataEditor.Services;
using Xunit;

namespace VideoMetadataEditor.Tests;

/// <summary>
/// Build 166: readable OpenSubtitles errors and the one-copy-per-build guard.
/// </summary>
public class Build166FixTests
{
    // ── OpenSubtitlesService.Describe ──────────────────────────────────────────

    [Fact]
    public void Describe_IncludesInnerExceptionInsteadOfSeeInner()
    {
        var ex = new HttpRequestException(
            "The SSL connection could not be established, see inner exception.",
            new System.IO.IOException("Received an unexpected EOF or 0 bytes from the transport stream."));

        var text = OpenSubtitlesService.Describe(ex);

        Assert.DoesNotContain("see inner exception", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("The SSL connection could not be established", text);
        Assert.Contains("unexpected EOF", text);
        Assert.Contains("api.opensubtitles.com", text);   // the actionable hint
    }

    [Fact]
    public void Describe_UntrustedCertificate_SaysInterceptedNotTemporary()
    {
        // The exact chain seen on a real PC in build 166 testing.
        var ex = new HttpRequestException(
            "The SSL connection could not be established, see inner exception.",
            new System.Security.Authentication.AuthenticationException(
                "The remote certificate is invalid because of errors in the certificate chain: UntrustedRoot"));

        var text = OpenSubtitlesService.Describe(ex);

        Assert.Contains("UntrustedRoot", text);
        Assert.Contains("intercepting", text);
        Assert.Contains("Retrying won't help", text);
        Assert.DoesNotContain("Usually temporary", text);
    }

    [Fact]
    public void Describe_PlainException_IsJustItsMessage()
    {
        Assert.Equal("boom", OpenSubtitlesService.Describe(new InvalidOperationException("boom")));
    }

    [Fact]
    public void Describe_DeduplicatesRepeatedMessages()
    {
        var ex = new Exception("same", new Exception("same"));
        Assert.Equal("same", OpenSubtitlesService.Describe(ex));
    }

    // ── SingleInstanceService ──────────────────────────────────────────────────

    [Fact]
    public void SecondCopyOfSameBuild_IsNotPrimary_AndWakesTheFirst()
    {
        var build = "test-" + Guid.NewGuid().ToString("N");
        using var first = SingleInstanceService.Acquire(build);
        Assert.True(first.IsPrimary);

        using var woke = new ManualResetEventSlim(false);
        first.ListenForShowRequests(() => woke.Set());

        using (var second = SingleInstanceService.Acquire(build))
        {
            Assert.False(second.IsPrimary);
            second.SignalPrimary();
        }

        Assert.True(woke.Wait(TimeSpan.FromSeconds(5)), "primary was not asked to show itself");
    }

    [Fact]
    public void DifferentBuilds_CanRunSideBySide()
    {
        var a = "test-a-" + Guid.NewGuid().ToString("N");
        var b = "test-b-" + Guid.NewGuid().ToString("N");
        using var first = SingleInstanceService.Acquire(a);
        using var second = SingleInstanceService.Acquire(b);
        Assert.True(first.IsPrimary);
        Assert.True(second.IsPrimary);
    }

    [Fact]
    public void NameIncludesBuildVersion()
    {
        Assert.Equal("VideoMetadataEditor_1.4.0.166", SingleInstanceService.NameFor("1.4.0.166"));
    }
}
