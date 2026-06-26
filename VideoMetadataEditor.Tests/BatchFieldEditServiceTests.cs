using VideoMetadataEditor.Services;
using VideoMetadataEditor.Models;
using Xunit;
using static VideoMetadataEditor.Services.BatchFieldEditService;

namespace VideoMetadataEditor.Tests;

/// <summary>
/// Build 123 — tests for BatchFieldEditService.ApplyEdits and CombineCsv (the pure
/// field-merge logic behind batch field editing). The dialog and write loop are not
/// covered here.
/// </summary>
public class BatchFieldEditServiceTests
{
    private static MovieMetadata Sample() => new()
    {
        Title = "Keep Me", EpisodeTitle = "Keep Me Too",
        Season = 2, Episode = 7, Description = "Keep this plot.",
        Year = "2010", Genre = "Action, Crime", Director = "Old Director",
        Cast = "Alice, Bob", MpaRating = "PG", ShowTitle = "Old Show",
        ImdbId = "tt999", IsWatched = false,
    };

    // ── Opt-in: untouched fields stay put ────────────────────────────────────────

    [Fact]
    public void NoEdits_LeavesEverythingUnchanged()
    {
        var m = Sample();
        ApplyEdits(m, new BatchFieldEdits());
        Assert.Equal("2010", m.Year);
        Assert.Equal("Action, Crime", m.Genre);
        Assert.Equal("Old Director", m.Director);
        Assert.Equal("Old Show", m.ShowTitle);
        Assert.False(m.IsWatched);
    }

    [Fact]
    public void OnlyTickedFields_AreChanged_PerFileFieldsUntouched()
    {
        var m = Sample();
        ApplyEdits(m, new BatchFieldEdits { Director = "New Director" });
        Assert.Equal("New Director", m.Director);
        // Everything per-file stays intact.
        Assert.Equal("Keep Me", m.Title);
        Assert.Equal("Keep Me Too", m.EpisodeTitle);
        Assert.Equal(7, m.Episode);
        Assert.Equal("Keep this plot.", m.Description);
        Assert.Equal("tt999", m.ImdbId);
    }

    // ── Single-value replaces ────────────────────────────────────────────────────

    [Fact]
    public void Year_Director_Mpa_ShowTitle_Replace()
    {
        var m = Sample();
        ApplyEdits(m, new BatchFieldEdits
        {
            Year = "1999", Director = "Lana", MpaRating = "R", ShowTitle = "New Show",
        });
        Assert.Equal("1999", m.Year);
        Assert.Equal("Lana", m.Director);
        Assert.Equal("R", m.MpaRating);
        Assert.Equal("New Show", m.ShowTitle);
    }

    // ── Multi-value: Replace vs Append ───────────────────────────────────────────

    [Fact]
    public void Genre_Replace_Overwrites()
    {
        var m = Sample();
        ApplyEdits(m, new BatchFieldEdits { Genre = "Anime", GenreMode = TextMode.Replace });
        Assert.Equal("Anime", m.Genre);
    }

    [Fact]
    public void Genre_Append_AddsNewDeduped()
    {
        var m = Sample(); // Action, Crime
        ApplyEdits(m, new BatchFieldEdits { Genre = "Drama, Crime", GenreMode = TextMode.Append });
        Assert.Equal("Action, Crime, Drama", m.Genre); // Crime not duplicated
    }

    [Fact]
    public void Cast_Append_CaseInsensitiveDedup()
    {
        var m = Sample(); // Alice, Bob
        ApplyEdits(m, new BatchFieldEdits { Cast = "bob, Carol", CastMode = TextMode.Append });
        Assert.Equal("Alice, Bob, Carol", m.Cast); // "bob" deduped against "Bob"
    }

    [Fact]
    public void Genre_Append_FromEmptyExisting()
    {
        var m = Sample();
        m.Genre = "";
        ApplyEdits(m, new BatchFieldEdits { Genre = "Sci-Fi, Thriller", GenreMode = TextMode.Append });
        Assert.Equal("Sci-Fi, Thriller", m.Genre);
    }

    [Fact]
    public void Genre_Replace_WithEmpty_Clears()
    {
        var m = Sample();
        ApplyEdits(m, new BatchFieldEdits { Genre = "", GenreMode = TextMode.Replace });
        Assert.Equal("", m.Genre);
    }

    // ── Watched tri-state ────────────────────────────────────────────────────────

    [Fact]
    public void Watched_SetWatched()
    {
        var m = Sample();
        ApplyEdits(m, new BatchFieldEdits { Watched = WatchedChange.SetWatched });
        Assert.True(m.IsWatched);
    }

    [Fact]
    public void Watched_SetUnwatched()
    {
        var m = Sample();
        m.IsWatched = true;
        ApplyEdits(m, new BatchFieldEdits { Watched = WatchedChange.SetUnwatched });
        Assert.False(m.IsWatched);
    }

    [Fact]
    public void Watched_Leave_DoesNotChange()
    {
        var m = Sample();
        m.IsWatched = true;
        ApplyEdits(m, new BatchFieldEdits { Watched = WatchedChange.Leave });
        Assert.True(m.IsWatched);
    }

    // ── HasAnyChange gate ────────────────────────────────────────────────────────

    [Fact]
    public void HasAnyChange_FalseForEmpty_TrueWhenAnySet()
    {
        Assert.False(new BatchFieldEdits().HasAnyChange);
        Assert.True(new BatchFieldEdits { Director = "x" }.HasAnyChange);
        Assert.True(new BatchFieldEdits { Genre = "" }.HasAnyChange); // empty string still = "change to empty"
        Assert.True(new BatchFieldEdits { Watched = WatchedChange.SetWatched }.HasAnyChange);
    }

    // ── CombineCsv direct ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Action, Crime", "Drama", TextMode.Replace, "Drama")]
    [InlineData("Action, Crime", "Drama, Crime", TextMode.Append, "Action, Crime, Drama")]
    [InlineData(null, "X, Y", TextMode.Append, "X, Y")]
    [InlineData("  A , B ", "C", TextMode.Append, "A, B, C")]
    public void CombineCsv_Cases(string? existing, string incoming, TextMode mode, string expected)
    {
        Assert.Equal(expected, CombineCsv(existing, incoming, mode));
    }

    // ── Null-safety ──────────────────────────────────────────────────────────────

    [Fact]
    public void ApplyEdits_NullArgs_DoNotThrow()
    {
        ApplyEdits(null!, new BatchFieldEdits { Year = "2000" });
        ApplyEdits(Sample(), null!);
    }
}
