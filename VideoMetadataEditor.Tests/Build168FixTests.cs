using System;
using System.IO;
using VideoMetadataEditor.Services;
using Xunit;

namespace VideoMetadataEditor.Tests;

/// <summary>
/// Build 168: VME temp files are never treated as videos, stranded faststart temps map
/// back to their original name, and the extra-library-folder list can't hold duplicates.
/// </summary>
public class Build168FixTests
{
    [Theory]
    [InlineData(@"A:\TV\Rugal - S01E09 - Episode 9.faststart.tmp.mp4", true)]
    [InlineData(@"A:\TV\Rugal - S01E09 - Episode 9.faststart.tmp.nfo", true)]
    [InlineData(@"A:\TV\Rugal - S01E09 - Episode 9.faststart.tmp (1).mp4", true)]
    [InlineData(@"A:\TV\Movie.remux.mkv", true)]
    [InlineData(@"A:\TV\Movie.remux (2).mp4", true)]
    [InlineData(@"A:\TV\.vme_tmp_abc.mp4", true)]
    [InlineData(@"A:\TV\Rugal - S01E09 - Episode 9.mp4", false)]
    [InlineData(@"A:\Movies\The Remux King (2020).mp4", false)]   // "remux" in a real title
    [InlineData(@"A:\Movies\Remuxed.mp4", false)]
    public void IsWorkingFile(string path, bool expected)
        => Assert.Equal(expected, WorkingFileFilter.IsWorkingFile(path));

    [Fact]
    public void FaststartOriginalFor_MapsBackToOriginalName()
    {
        var temp = Path.Combine("A:", "TV", "Rugal - S01E09 - Episode 9.faststart.tmp.mp4");
        var expected = Path.Combine("A:", "TV", "Rugal - S01E09 - Episode 9.mp4");
        Assert.Equal(expected, WorkingFileFilter.FaststartOriginalFor(temp));

        var numbered = Path.Combine("A:", "TV", "Show.faststart.tmp (2).mp4");
        Assert.Equal(Path.Combine("A:", "TV", "Show.mp4"), WorkingFileFilter.FaststartOriginalFor(numbered));

        Assert.Null(WorkingFileFilter.FaststartOriginalFor(Path.Combine("A:", "TV", "Show.mp4")));
    }

    [Fact]
    public void Dedupe_RemovesDuplicatesCaseAndTrailingSlash()
    {
        var result = LibraryFolderList.Dedupe(
            new[] { @"A:\TV Shows", @"A:\TV Shows", @"a:\tv shows\", @"A:/TV Shows", @"D:\Anime", "" },
            primary: @"A:\Movies");
        Assert.Equal(new[] { @"A:\TV Shows", @"D:\Anime" }, result);
    }

    [Fact]
    public void Dedupe_DropsThePrimaryFolder()
    {
        var result = LibraryFolderList.Dedupe(new[] { @"A:\Movies\", @"A:\TV Shows" }, primary: @"A:\Movies");
        Assert.Equal(new[] { @"A:\TV Shows" }, result);
    }

    [Fact]
    public void Normalize_KeepsDriveRoot()
    {
        Assert.Equal(@"A:\", LibraryFolderList.Normalize(@"a:\"));
        Assert.Equal(@"A:\TV SHOWS", LibraryFolderList.Normalize(@"a:\TV Shows\\"));
    }
}
