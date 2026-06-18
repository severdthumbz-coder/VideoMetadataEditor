using System.Text;
using System.Text.RegularExpressions;

namespace VideoMetadataEditor.Services;

/// <summary>
/// Encodes and decodes the custom [VME:KEY=value] token format that VME stores
/// in the Comment field of video files. Pure static methods, no file I/O, no
/// external dependencies — designed to be directly compile-included in the test
/// project for round-trip verification without needing TagLib or WPF.
///
/// Format: description text (optional) followed by packed tokens on a new line:
///   [VME:IMDB=tt1234567][VME:RATING=8.5][VME:MPA=PG-13][VME:WATCHED=1]…
/// </summary>
public static class VmeCommentCodec
{
    // ── Encode ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the full comment string from metadata fields.
    /// </summary>
    public static string Encode(
        string description    = "",
        string imdbId         = "",
        string tmdbId         = "",
        float  rating         = 0f,
        string mpaRating      = "",
        bool   isWatched      = false,
        bool   isEpisode      = false,
        string showTitle      = "",
        int?   season         = null,
        int?   episode        = null,
        string episodeTitle   = "",
        string airedDate      = "",
        string tvdbId         = "",
        string tmdbSeriesId   = "")
    {
        var body = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(description))
            body.Append(description.Trim());

        var tokens = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(imdbId))
            tokens.Append($"[VME:IMDB={imdbId.Trim()}]");
        if (!string.IsNullOrWhiteSpace(tmdbId))
            tokens.Append($"[VME:TMDB={tmdbId.Trim()}]");
        if (rating > 0f)
            tokens.Append($"[VME:RATING={rating.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}]");
        if (!string.IsNullOrWhiteSpace(mpaRating))
            tokens.Append($"[VME:MPA={mpaRating}]");
        if (isWatched)
            tokens.Append("[VME:WATCHED=1]");

        if (isEpisode)
        {
            tokens.Append("[VME:EP_MODE=1]");
            if (!string.IsNullOrWhiteSpace(showTitle))  tokens.Append($"[VME:SHOW={showTitle}]");
            if (season.HasValue)                         tokens.Append($"[VME:SEASON={season}]");
            if (episode.HasValue)                        tokens.Append($"[VME:EPISODE={episode}]");
            if (!string.IsNullOrWhiteSpace(episodeTitle)) tokens.Append($"[VME:ETITLE={episodeTitle}]");
            if (!string.IsNullOrWhiteSpace(airedDate))   tokens.Append($"[VME:AIRED={airedDate}]");
            if (!string.IsNullOrWhiteSpace(tvdbId))      tokens.Append($"[VME:TVDB={tvdbId}]");
            if (!string.IsNullOrWhiteSpace(tmdbSeriesId)) tokens.Append($"[VME:TMDB_SERIES={tmdbSeriesId}]");
        }

        if (tokens.Length > 0)
        {
            if (body.Length > 0) body.Append('\n');
            body.Append(tokens);
        }
        return body.ToString();
    }

    // ── Decode ────────────────────────────────────────────────────────────────

    /// <summary>Extracts a single token value from a comment string.</summary>
    public static string Get(string comment, string key)
    {
        if (string.IsNullOrWhiteSpace(comment)) return string.Empty;
        var match = Regex.Match(comment, $@"\[VME:{Regex.Escape(key)}=([^\]]*)\]");
        return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
    }

    /// <summary>Returns the description text with all VME tokens stripped.</summary>
    public static string GetDescription(string comment)
    {
        if (string.IsNullOrWhiteSpace(comment)) return string.Empty;
        return Regex.Replace(comment, @"\[VME:[A-Z_]+=([^\]]*)\]", string.Empty)
                    .Trim('\n', '\r', ' ');
    }

    /// <summary>
    /// Decodes all fields from a comment string into a decoded result.
    /// Mirrors the field parsing in MetadataService.ReadMetadataCore.
    /// </summary>
    public static DecodedComment Decode(string comment)
    {
        // Parse conditionals before the object initialiser so init-only
        // properties can be set in a single expression
        float.TryParse(Get(comment, "RATING"),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float rating);
        int.TryParse(Get(comment, "SEASON"),  out int season);
        int.TryParse(Get(comment, "EPISODE"), out int episode);

        return new DecodedComment
        {
            Description  = GetDescription(comment),
            ImdbId       = Get(comment, "IMDB"),
            TmdbId       = Get(comment, "TMDB"),
            Rating       = rating,
            MpaRating    = Get(comment, "MPA"),
            IsWatched    = Get(comment, "WATCHED")  == "1",
            IsEpisode    = Get(comment, "EP_MODE")  == "1",
            ShowTitle    = Get(comment, "SHOW"),
            Season       = season  > 0 ? season  : null,
            Episode      = episode > 0 ? episode : null,
            EpisodeTitle = Get(comment, "ETITLE"),
            AiredDate    = Get(comment, "AIRED"),
            TvdbId       = Get(comment, "TVDB"),
            TmdbSeriesId = Get(comment, "TMDB_SERIES"),
        };
    }

    public record DecodedComment
    {
        public string Description  { get; init; } = "";
        public string ImdbId       { get; init; } = "";
        public string TmdbId       { get; init; } = "";
        public float  Rating       { get; init; }
        public string MpaRating    { get; init; } = "";
        public bool   IsWatched    { get; init; }
        public bool   IsEpisode    { get; init; }
        public string ShowTitle    { get; init; } = "";
        public int?   Season       { get; init; }
        public int?   Episode      { get; init; }
        public string EpisodeTitle { get; init; } = "";
        public string AiredDate    { get; init; } = "";
        public string TvdbId       { get; init; } = "";
        public string TmdbSeriesId { get; init; } = "";
    }

    // ── Post-write verification ───────────────────────────────────────────────

    /// <summary>
    /// Compares the VME token fields that were intended for a write against what
    /// was actually read back from the file's comment after the write. Returns the
    /// list of field names that did NOT persist correctly (empty list = full match).
    ///
    /// Pure logic, no file I/O — so the verification contract is unit-testable in CI
    /// without needing TagLib# to open a real container. Only fields that were
    /// actually set in the intended write are checked; a field the user left blank is
    /// not expected to appear on disk and is never reported as a mismatch.
    ///
    /// String comparisons are case-insensitive and whitespace-trimmed because some
    /// containers normalise tag text. Rating is compared at one-decimal precision to
    /// match how it is encoded.
    /// </summary>
    public static IReadOnlyList<string> VerifyWritten(
        string intendedComment, string readBackComment)
    {
        var want = Decode(intendedComment);
        var got  = Decode(readBackComment);
        var mismatches = new List<string>();

        void CheckStr(string name, string intended, string actual)
        {
            if (string.IsNullOrWhiteSpace(intended)) return; // not set → not expected
            if (!string.Equals(intended.Trim(), actual.Trim(), StringComparison.OrdinalIgnoreCase))
                mismatches.Add(name);
        }

        CheckStr("IMDB ID",       want.ImdbId,       got.ImdbId);
        CheckStr("TMDB ID",       want.TmdbId,       got.TmdbId);
        CheckStr("MPA rating",    want.MpaRating,    got.MpaRating);
        // NOTE: Description is intentionally NOT verified here. It is free-text and
        // some containers truncate or normalise long comment atoms, which would fail
        // an otherwise-correct write. The structured tokens below are what matter for
        // data integrity; description is best-effort.

        if (want.Rating > 0f && Math.Abs(want.Rating - got.Rating) > 0.05f)
            mismatches.Add("Rating");

        // Watched is only asserted when the user set it true (false writes no token).
        if (want.IsWatched && !got.IsWatched)
            mismatches.Add("Watched flag");

        if (want.IsEpisode)
        {
            if (!got.IsEpisode) mismatches.Add("Episode flag");
            CheckStr("Show title",    want.ShowTitle,    got.ShowTitle);
            CheckStr("Episode title", want.EpisodeTitle, got.EpisodeTitle);
            CheckStr("Aired date",    want.AiredDate,    got.AiredDate);
            CheckStr("TVDB ID",       want.TvdbId,       got.TvdbId);
            CheckStr("TMDB series ID",want.TmdbSeriesId, got.TmdbSeriesId);
            if (want.Season.HasValue  && want.Season  != got.Season)  mismatches.Add("Season");
            if (want.Episode.HasValue && want.Episode != got.Episode) mismatches.Add("Episode number");
        }

        return mismatches;
    }
}
