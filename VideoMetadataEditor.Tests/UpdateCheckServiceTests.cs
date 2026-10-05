using System;
using VideoMetadataEditor.Services;
using Xunit;

namespace VideoMetadataEditor.Tests;

/// <summary>
/// Tests for UpdateCheckService's pure logic: tag parsing and the newer-version
/// decision. The network fetch (CheckAsync) is not tested here — it's a thin wrapper
/// that fails silent; the meaningful logic is ParseTag + Evaluate.
/// </summary>
public class UpdateCheckServiceTests
{
    // ── ParseTag ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("v1.4.0.130", "1.4.0.130")]
    [InlineData("1.4.0.130",  "1.4.0.130")]
    [InlineData(" v1.4.0.130 ", "1.4.0.130")]
    [InlineData("V1.4.0.130", "1.4.0.130")]
    public void ParseTag_AcceptsValidTags(string tag, string expected)
    {
        var v = UpdateCheckService.ParseTag(tag);
        Assert.NotNull(v);
        Assert.Equal(Version.Parse(expected), v);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("v")]
    [InlineData("latest")]
    [InlineData("release-2026")]
    public void ParseTag_RejectsInvalidTags(string? tag)
    {
        Assert.Null(UpdateCheckService.ParseTag(tag));
    }

    // ── Evaluate: newer / not newer ──────────────────────────────────────────────

    [Fact]
    public void Evaluate_NewerRemote_ReturnsUpdate()
    {
        var json = @"{""tag_name"":""v1.4.0.130"",""html_url"":""https://github.com/x/y/releases/tag/v1.4.0.130""}";
        var info = UpdateCheckService.Evaluate(json, new Version(1, 4, 0, 129));
        Assert.NotNull(info);
        Assert.True(info!.IsNewer);
        Assert.Equal(new Version(1, 4, 0, 130), info.LatestVersion);
        Assert.Contains("1.4.0.130", info.ReleaseUrl);
    }

    [Fact]
    public void Evaluate_EqualVersion_ReturnsNull()
    {
        var json = @"{""tag_name"":""v1.4.0.129"",""html_url"":""https://x""}";
        Assert.Null(UpdateCheckService.Evaluate(json, new Version(1, 4, 0, 129)));
    }

    [Fact]
    public void Evaluate_OlderRemote_ReturnsNull()
    {
        var json = @"{""tag_name"":""v1.4.0.128"",""html_url"":""https://x""}";
        Assert.Null(UpdateCheckService.Evaluate(json, new Version(1, 4, 0, 129)));
    }

    [Fact]
    public void Evaluate_NewerMajorMinor_ReturnsUpdate()
    {
        var json = @"{""tag_name"":""v1.5.0.0"",""html_url"":""https://x""}";
        var info = UpdateCheckService.Evaluate(json, new Version(1, 4, 0, 129));
        Assert.NotNull(info);
        Assert.Equal(new Version(1, 5, 0, 0), info!.LatestVersion);
    }

    // ── Evaluate: malformed / missing input ──────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData(@"{""name"":""no tag here""}")]
    [InlineData(@"{""tag_name"":""latest""}")]     // unparseable tag
    public void Evaluate_MalformedOrMissing_ReturnsNull(string json)
    {
        Assert.Null(UpdateCheckService.Evaluate(json, new Version(1, 4, 0, 129)));
    }

    [Fact]
    public void Evaluate_MissingHtmlUrl_FallsBackToReleasesPage()
    {
        // Newer tag but no html_url in the body → still an update, with the fallback URL.
        var json = @"{""tag_name"":""v2.0.0.0""}";
        var info = UpdateCheckService.Evaluate(json, new Version(1, 4, 0, 129));
        Assert.NotNull(info);
        Assert.Equal(UpdateCheckService.ReleasesPage, info!.ReleaseUrl);
    }

    [Fact]
    public void Evaluate_NullCurrent_ReturnsNull()
    {
        var json = @"{""tag_name"":""v2.0.0.0"",""html_url"":""https://x""}";
        Assert.Null(UpdateCheckService.Evaluate(json, null!));
    }

    // ── ParseTagFromLocation (redirect-based check, Build 164) ───────────────────

    [Theory]
    [InlineData("https://github.com/severdthumbz-coder/VideoMetadataEditor/releases/tag/v1.4.0.163", "v1.4.0.163")]
    [InlineData("/severdthumbz-coder/VideoMetadataEditor/releases/tag/v1.4.0.163",                   "v1.4.0.163")]
    [InlineData("https://github.com/o/r/releases/tag/v1.4.0.163/",                                    "v1.4.0.163")]
    [InlineData("https://github.com/o/r/releases/tag/v1.4.0.163?x=1",                                 "v1.4.0.163")]
    public void ParseTagFromLocation_ExtractsTag(string location, string expected)
    {
        Assert.Equal(expected, UpdateCheckService.ParseTagFromLocation(location));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://github.com/o/r/releases")]      // no releases → GitHub redirects here
    [InlineData("https://github.com/o/r/releases/tag/")]
    public void ParseTagFromLocation_ReturnsNullWhenNoTag(string? location)
    {
        Assert.Null(UpdateCheckService.ParseTagFromLocation(location));
    }

    [Fact]
    public void ParseTagFromLocation_FeedsParseTag()
    {
        var tag = UpdateCheckService.ParseTagFromLocation(
            "https://github.com/o/r/releases/tag/v1.4.0.163");
        Assert.Equal(new Version(1, 4, 0, 163), UpdateCheckService.ParseTag(tag));
    }
}
