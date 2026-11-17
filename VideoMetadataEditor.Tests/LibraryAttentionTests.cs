using VideoMetadataEditor.Models;
using Xunit;

namespace VideoMetadataEditor.Tests;

/// <summary>Build 172: the Library "Show" filter's definitions.</summary>
public class LibraryAttentionTests
{
    [Fact]
    public void Untagged_WhenNoIdsGenreOrSynopsis()
    {
        var e = new LibraryEntry { Title = "Some File Name" };   // title alone = filename fallback
        Assert.True(e.IsUntagged);
    }

    [Theory]
    [InlineData("tt123", "", "", "", "")]
    [InlineData("", "550", "", "", "")]
    [InlineData("", "", "1399", "", "")]
    [InlineData("", "", "", "Drama", "")]
    [InlineData("", "", "", "", "A synopsis")]
    public void NotUntagged_WhenAnyMetadataPresent(string imdb, string tmdb, string series, string genre, string desc)
    {
        var e = new LibraryEntry
        {
            ImdbId = imdb, TmdbId = tmdb, TmdbSeriesId = series, Genre = genre, Description = desc
        };
        Assert.False(e.IsUntagged);
    }

    [Fact]
    public void MissingArtwork_OnlyOnceChecked()
    {
        var e = new LibraryEntry();
        Assert.False(e.IsMissingArtwork);        // not checked yet — may still be loading
        e.ArtworkChecked = true;
        Assert.True(e.IsMissingArtwork);
    }

    [Fact]
    public void NotMissingArtwork_WhenArtPresent()
    {
        var e = new LibraryEntry { ArtworkChecked = true, CoverArt = new byte[] { 1, 2, 3 } };
        Assert.False(e.IsMissingArtwork);
    }
}
