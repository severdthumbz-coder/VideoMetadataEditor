using System.Linq;
using VideoMetadataEditor.Services;
using Xunit;

namespace VideoMetadataEditor.Tests;

/// <summary>Build 170: Files panel natural ordering.</summary>
public class NaturalStringComparerTests
{
    [Fact]
    public void SortsEpisodesByNumberValue()
    {
        var input = new[]
        {
            "Hyper Knife - S01E08 - Episode 8.mkv",
            "Hyper Knife - S01E02 - Episode 2.mkv",
            "Hyper Knife - S01E10 - Episode 10.mkv",
            "Hyper Knife - S01E01 - Episode 1.mkv",
        };
        var sorted = input.OrderBy(s => s, NaturalStringComparer.Instance).ToArray();
        Assert.Equal(new[]
        {
            "Hyper Knife - S01E01 - Episode 1.mkv",
            "Hyper Knife - S01E02 - Episode 2.mkv",
            "Hyper Knife - S01E08 - Episode 8.mkv",
            "Hyper Knife - S01E10 - Episode 10.mkv",
        }, sorted);
    }

    [Fact]
    public void TwoComesBeforeTen_WithoutZeroPadding()
    {
        Assert.True(NaturalStringComparer.Instance.Compare("Episode 2", "Episode 10") < 0);
        Assert.True(NaturalStringComparer.Instance.Compare("Episode 10", "Episode 2") > 0);
    }

    [Fact]
    public void CaseInsensitiveText()
    {
        Assert.True(NaturalStringComparer.Instance.Compare("alpha", "Beta") < 0);
        Assert.Equal(0, NaturalStringComparer.Instance.Compare("Movie", "movie"));
    }

    [Fact]
    public void HandlesNullsAndPrefixes()
    {
        Assert.True(NaturalStringComparer.Instance.Compare(null, "a") < 0);
        Assert.True(NaturalStringComparer.Instance.Compare("Show", "Show 2") < 0);
        Assert.Equal(0, NaturalStringComparer.Instance.Compare("x", "x"));
    }

    [Fact]
    public void VeryLongNumbersDoNotOverflow()
    {
        Assert.True(NaturalStringComparer.Instance.Compare(
            "id 99999999999999999999998", "id 99999999999999999999999") < 0);
    }
}
