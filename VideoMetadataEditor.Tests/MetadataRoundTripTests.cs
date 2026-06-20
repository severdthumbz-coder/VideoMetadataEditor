using VideoMetadataEditor.Services;
using Xunit;

namespace VideoMetadataEditor.Tests;

/// <summary>
/// Round-trip tests for VmeCommentCodec — the encode/decode logic that stores
/// VME metadata (ratings, IDs, TV info) as [VME:KEY=value] tokens in the
/// Comment field of video files.
///
/// These tests verify the core data integrity contract: a value written through
/// Encode() must come back identically through Decode(). This is the most
/// critical correctness guarantee in the app — if it breaks, all embedded
/// metadata silently survives in the file but reads back wrong.
/// </summary>
public class MetadataRoundTripTests
{
    // ── Encode / Decode round-trips ───────────────────────────────────────────

    [Fact]
    public void Movie_AllFields_RoundTrip()
    {
        var comment = VmeCommentCodec.Encode(
            description : "A film about dreaming.",
            imdbId      : "tt1375666",
            tmdbId      : "27205",
            rating      : 8.8f,
            mpaRating   : "PG-13",
            isWatched   : true);

        var d = VmeCommentCodec.Decode(comment);

        Assert.Equal("A film about dreaming.", d.Description);
        Assert.Equal("tt1375666", d.ImdbId);
        Assert.Equal("27205",     d.TmdbId);
        Assert.Equal(8.8f,        d.Rating, precision: 1);
        Assert.Equal("PG-13",     d.MpaRating);
        Assert.True(d.IsWatched);
        Assert.False(d.IsEpisode);
    }

    [Fact]
    public void TvEpisode_AllFields_RoundTrip()
    {
        var comment = VmeCommentCodec.Encode(
            isEpisode    : true,
            showTitle    : "Breaking Bad",
            season       : 2,
            episode      : 7,
            episodeTitle : "Negro y Azul",
            airedDate    : "2009-04-24",
            tvdbId       : "3572115",
            tmdbSeriesId : "1396");

        var d = VmeCommentCodec.Decode(comment);

        Assert.True(d.IsEpisode);
        Assert.Equal("Breaking Bad",  d.ShowTitle);
        Assert.Equal(2,               d.Season);
        Assert.Equal(7,               d.Episode);
        Assert.Equal("Negro y Azul",  d.EpisodeTitle);
        Assert.Equal("2009-04-24",    d.AiredDate);
        Assert.Equal("3572115",       d.TvdbId);
        Assert.Equal("1396",          d.TmdbSeriesId);
    }

    [Fact]
    public void EmptyComment_ReturnsAllDefaults()
    {
        var d = VmeCommentCodec.Decode("");

        Assert.Equal("", d.ImdbId);
        Assert.Equal("", d.TmdbId);
        Assert.Equal(0f, d.Rating);
        Assert.Equal("", d.MpaRating);
        Assert.False(d.IsWatched);
        Assert.False(d.IsEpisode);
    }

    [Fact]
    public void DescriptionPreservedThroughTokens()
    {
        // Description + tokens must survive a round-trip with the description intact
        var comment = VmeCommentCodec.Encode(
            description : "Multi-line\ndescription with special chars: &<>\"'",
            imdbId      : "tt0111161");

        var d = VmeCommentCodec.Decode(comment);

        Assert.Equal("Multi-line\ndescription with special chars: &<>\"'", d.Description);
        Assert.Equal("tt0111161", d.ImdbId);
    }

    [Fact]
    public void NoDescription_TokensOnly_RoundTrip()
    {
        var comment = VmeCommentCodec.Encode(imdbId: "tt0068646", rating: 9.2f);
        var d       = VmeCommentCodec.Decode(comment);

        Assert.Equal("",           d.Description);
        Assert.Equal("tt0068646",  d.ImdbId);
        Assert.Equal(9.2f,         d.Rating, precision: 1);
    }

    // ── Token isolation ───────────────────────────────────────────────────────

    [Fact]
    public void GetDescription_StripsAllTokens()
    {
        var comment = "A great movie\n[VME:IMDB=tt1234][VME:RATING=7.5][VME:MPA=R]";
        Assert.Equal("A great movie", VmeCommentCodec.GetDescription(comment));
    }

    [Fact]
    public void Get_MissingKey_ReturnsEmpty()
    {
        var comment = VmeCommentCodec.Encode(imdbId: "tt0111161");
        Assert.Equal("", VmeCommentCodec.Get(comment, "TMDB"));
        Assert.Equal("", VmeCommentCodec.Get(comment, "RATING"));
        Assert.Equal("", VmeCommentCodec.Get(comment, "NONEXISTENT"));
    }

    [Fact]
    public void Rating_StoredAsInvariantDecimal()
    {
        // Must use '.' not ',' regardless of system locale
        var comment = VmeCommentCodec.Encode(rating: 8.5f);
        Assert.Contains("[VME:RATING=8.5]", comment);
        // And read back correctly
        var d = VmeCommentCodec.Decode(comment);
        Assert.Equal(8.5f, d.Rating, precision: 1);
    }

    [Fact]
    public void WatchedFlag_TrueWritesToken_FalseWritesNothing()
    {
        var watched   = VmeCommentCodec.Encode(isWatched: true);
        var unwatched = VmeCommentCodec.Encode(isWatched: false);

        Assert.Contains("[VME:WATCHED=1]", watched);
        Assert.DoesNotContain("WATCHED",   unwatched);
    }

    [Fact]
    public void SeasonEpisode_ZeroPaddingNotRequired_ParsesCorrectly()
    {
        // Token stores raw int; display formatting is a separate concern
        var comment = VmeCommentCodec.Encode(isEpisode: true, season: 1, episode: 3);
        var d       = VmeCommentCodec.Decode(comment);
        Assert.Equal(1, d.Season);
        Assert.Equal(3, d.Episode);
    }

    // ── Post-write verification (VerifyWritten) ───────────────────────────────

    [Fact]
    public void Verify_IdenticalComment_NoMismatches()
    {
        var c = VmeCommentCodec.Encode(
            imdbId: "tt1375666", tmdbId: "27205", rating: 8.8f,
            mpaRating: "PG-13", isWatched: true);
        Assert.Empty(VmeCommentCodec.VerifyWritten(c, c));
    }

    [Fact]
    public void Verify_DroppedRating_ReportedAsMismatch()
    {
        var intended = VmeCommentCodec.Encode(imdbId: "tt1", rating: 7.5f);
        // Disk got the ID but the rating token was silently dropped
        var onDisk   = VmeCommentCodec.Encode(imdbId: "tt1");
        var missing  = VmeCommentCodec.VerifyWritten(intended, onDisk);
        Assert.Contains("Rating", missing);
        Assert.DoesNotContain("IMDB ID", missing);
    }

    [Fact]
    public void Verify_DroppedEpisodeFields_ReportedAsMismatch()
    {
        var intended = VmeCommentCodec.Encode(
            isEpisode: true, showTitle: "Dr. Stone", season: 2, episode: 4,
            episodeTitle: "Stone Wars");
        // Disk lost everything episode-related (the exact silent-revert scenario)
        var onDisk   = VmeCommentCodec.Encode();
        var missing  = VmeCommentCodec.VerifyWritten(intended, onDisk);
        Assert.Contains("Episode flag", missing);
        Assert.Contains("Show title",   missing);
        Assert.Contains("Season",       missing);
        Assert.Contains("Episode number", missing);
    }

    [Fact]
    public void Verify_UnsetFields_NotReported()
    {
        // Intended write set only the IMDB ID; everything else blank.
        var intended = VmeCommentCodec.Encode(imdbId: "tt9");
        var onDisk   = VmeCommentCodec.Encode(imdbId: "tt9");
        // No false positives for fields the user never set.
        Assert.Empty(VmeCommentCodec.VerifyWritten(intended, onDisk));
    }

    [Fact]
    public void Verify_CaseAndWhitespaceInsensitive_ForText()
    {
        var intended = VmeCommentCodec.Encode(mpaRating: "PG-13");
        var onDisk   = VmeCommentCodec.Encode(mpaRating: " pg-13 ");
        Assert.Empty(VmeCommentCodec.VerifyWritten(intended, onDisk));
    }

    [Fact]
    public void Verify_WatchedFalse_NeverReported()
    {
        // Watched=false writes no token; verification must not demand it on disk.
        var intended = VmeCommentCodec.Encode(isWatched: false, imdbId: "tt1");
        var onDisk   = VmeCommentCodec.Encode(imdbId: "tt1");
        Assert.Empty(VmeCommentCodec.VerifyWritten(intended, onDisk));
    }

    // ── Semicolon truncation regression (the "A Taste of Hunger" / Dr. STONE bug) ──
    // TagLib# 2.3.0 truncates the MP4 comment atom at the first ';' on write, which
    // silently dropped every VME token (they sit after the description). Encode must
    // neutralize semicolons so the token block always survives.

    [Fact]
    public void Encode_SemicolonInDescription_NoSemicolonInOutput()
    {
        var desc = "Maggie and Carsten run their own gourmet restaurant; Malus. They want it all.";
        var c = VmeCommentCodec.Encode(description: desc, imdbId: "tt11188560", tmdbId: "766105",
            rating: 6.0f, mpaRating: "NR");
        Assert.DoesNotContain(';', c);                 // the truncation trigger is gone
        Assert.Contains("[VME:IMDB=tt11188560]", c);   // tokens survive
        Assert.Contains("[VME:TMDB=766105]", c);
    }

    [Fact]
    public void Encode_SemicolonDescription_TokensStillDecode()
    {
        // The real failing movie description.
        var desc = "Maggie and Carsten love each other; they run their own gourmet restaurant; Malus.";
        var c = VmeCommentCodec.Encode(description: desc, imdbId: "tt11188560", tmdbId: "766105",
            rating: 6.0f, mpaRating: "NR");
        var d = VmeCommentCodec.Decode(c);
        Assert.Equal("tt11188560", d.ImdbId);
        Assert.Equal("766105", d.TmdbId);
        Assert.Equal("NR", d.MpaRating);
        Assert.Equal(6.0f, d.Rating);
    }

    [Fact]
    public void Encode_SemicolonInEpisodeDescription_EpisodeTokensSurvive()
    {
        // The real failing Dr. STONE episode description.
        var desc = "Chrome and Magma struggle with Ukyo; Senku gets busy building an invention; a new hope.";
        var c = VmeCommentCodec.Encode(description: desc, imdbId: "tt9679542", tmdbId: "2617654",
            rating: 7.2f, mpaRating: "TV-14", isEpisode: true, showTitle: "Dr. STONE",
            season: 2, episode: 4, episodeTitle: "Full Assault", airedDate: "2021-02-04",
            tmdbSeriesId: "86031");
        Assert.DoesNotContain(';', c);
        var d = VmeCommentCodec.Decode(c);
        Assert.True(d.IsEpisode);
        Assert.Equal("Dr. STONE", d.ShowTitle);
        Assert.Equal(4, d.Episode);
        Assert.Equal("Full Assault", d.EpisodeTitle);
        Assert.Equal("86031", d.TmdbSeriesId);
    }

    [Fact]
    public void Sanitize_ReplacesSemicolonWithComma()
    {
        Assert.Equal("a, b, c", VmeCommentCodec.Sanitize("a; b; c"));
        Assert.DoesNotContain(';', VmeCommentCodec.Sanitize("x;y;z"));
    }
}
