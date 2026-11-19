using System;
using System.Collections.Generic;
using VideoMetadataEditor.Models;
using VideoMetadataEditor.Services;
using Xunit;
using K = VideoMetadataEditor.Services.AutoMatch.Kind;

namespace VideoMetadataEditor.Tests;

/// <summary>Build 178: when auto-embed embeds on its own, and when a file goes to the review queue.</summary>
public class AutoMatchTests
{
    private static SearchResult R(string title, string year, string id, double pop = 0) =>
        new() { Title = title, Year = year, TmdbId = id, Popularity = pop };

    private const double High = 0.80;

    [Fact]
    public void TwoEntries_SameTitleAndYear_GoToReview_WithBoth()   // the One Last Shot case
    {
        var d = AutoMatch.DecideMovie(new List<SearchResult>
        {
            R("One Last Shot", "2026", "1607127"),
            R("One Last Shot", "2026", "1826133"),
        }, "One Last Shot", "2026", High);

        Assert.Equal(K.Review, d.Kind);
        Assert.Contains("2 entries", d.Reason);
        Assert.Equal(2, d.Candidates.Count);
    }

    [Fact]
    public void OneClearMatch_IsEmbedded()
    {
        var d = AutoMatch.DecideMovie(new List<SearchResult>
        {
            R("Love Language", "2026", "1371730"),
            R("The Language of Love", "1970", "42"),
        }, "Love Language", "2026", High);

        Assert.Equal(K.Auto, d.Kind);
        Assert.Equal("1371730", d.Best!.TmdbId);
    }

    [Fact]
    public void SameTitle_DifferentYears_FilenameYearDecides()
    {
        var results = new List<SearchResult> { R("Dune", "1984", "841"), R("Dune", "2021", "438631") };
        var d = AutoMatch.DecideMovie(results, "Dune", "2021", High);
        Assert.Equal(K.Auto, d.Kind);
        Assert.Equal("438631", d.Best!.TmdbId);

        // No year in the filename → can't tell which Dune it is.
        var noYear = AutoMatch.DecideMovie(results, "Dune", null, High);
        Assert.Equal(K.Review, noYear.Kind);
        Assert.Contains("no year", noYear.Reason);
    }

    [Fact]
    public void ExactTitle_OneYearOff_IsFine_ButFarOff_IsReviewed()
    {
        Assert.Equal(K.Auto, AutoMatch.DecideMovie(new List<SearchResult> { R("The Beast", "2025", "1") },
            "The Beast", "2026", High).Kind);
        Assert.Equal(K.Review, AutoMatch.DecideMovie(new List<SearchResult> { R("The Beast", "1975", "2") },
            "The Beast", "2026", High).Kind);
    }

    [Fact]
    public void WeakMatch_IsReviewed_Garbage_IsNoMatch()
    {
        var weak = AutoMatch.DecideMovie(new List<SearchResult> { R("Last Shot at Love", "2026", "9") },
            "One Last Shot", "2026", High);
        Assert.Equal(K.Review, weak.Kind);
        Assert.NotEmpty(weak.Candidates);

        var none = AutoMatch.DecideMovie(new List<SearchResult> { R("Completely Different", "2026", "9") },
            "One Last Shot", "2026", High);
        Assert.Equal(K.NoMatch, none.Kind);

        Assert.Equal(K.NoMatch, AutoMatch.DecideMovie(new List<SearchResult>(), "x", null, High).Kind);
    }

    [Fact]
    public void AlwaysChooseMovies_SendsEvenClearMatchesToReview()
    {
        var d = AutoMatch.DecideMovie(new List<SearchResult> { R("Love Language", "2026", "1") },
            "Love Language", "2026", High, alwaysReview: true);
        Assert.Equal(K.Review, d.Kind);
        Assert.Equal("1", d.Best!.TmdbId);   // still preselected
    }

    [Fact]
    public void Shows_WithTheSameName_AreReviewed_UnlessTheLibraryKnowsWhichOne()
    {
        var results = new List<SearchResult> { R("Doctor Who", "2005", "57243", 90), R("Doctor Who", "1963", "121", 40) };

        var d = AutoMatch.DecideShow(results, "Doctor Who", High);
        Assert.Equal(K.Review, d.Kind);
        Assert.Contains("2 shows", d.Reason);

        var known = AutoMatch.DecideShow(results, "Doctor Who", High, new HashSet<string> { "121" });
        Assert.Equal(K.Auto, known.Kind);
        Assert.Equal("121", known.Best!.TmdbId);
        Assert.Contains("your other episodes", known.Reason);
    }

    [Fact]
    public void OneShow_IsEmbedded()
    {
        var d = AutoMatch.DecideShow(new List<SearchResult> { R("Hyper Knife", "2025", "245079") }, "Hyper Knife", High);
        Assert.Equal(K.Auto, d.Kind);
    }

    [Fact]
    public void ReleaseGroupTitleAndYear_DoNotCountAsIdentified()
    {
        Assert.False(AutoMatch.AlreadyIdentified(new MovieMetadata { Title = "One Last Shot", Year = "2026" }));
        Assert.True(AutoMatch.AlreadyIdentified(new MovieMetadata { Title = "x", TmdbId = "1826133" }));
        Assert.True(AutoMatch.AlreadyIdentified(new MovieMetadata { ImdbId = "tt44898377" }));
        Assert.True(AutoMatch.AlreadyIdentified(new MovieMetadata { IsEpisode = true, TmdbSeriesId = "245079" }));
        Assert.False(AutoMatch.AlreadyIdentified(null));
    }

    [Theory]
    [InlineData("Marvel's The Defenders", "Marvels.The.Defenders")]
    [InlineData("Marvel’s The Defenders", "Marvels The Defenders")]
    [InlineData("Dune (2021)", "dune")]
    public void Normalise_TreatsTheseAsTheSameTitle(string a, string b) =>
        Assert.Equal(1.0, AutoMatch.Score(a, b));

    [Fact]
    public void Thresholds_MatchTheSettingsLevels()
    {
        Assert.Equal(0.40, AutoMatch.ThresholdFor(0));
        Assert.Equal(0.80, AutoMatch.ThresholdFor(2));
        Assert.Equal(1.00, AutoMatch.ThresholdFor(9));   // clamped
    }
}
