using VideoMetadataEditor.Models;

namespace VideoMetadataEditor.Services;

/// <summary>
/// Applies a set of SHARED field values across many files (batch field editing).
/// The single public method <see cref="ApplyEdits"/> is pure — it mutates only the
/// fields the user opted into, leaving everything else (Title, Episode, IDs, plot,
/// per-file unique data) untouched. No UI, no disk: fully unit-testable.
///
/// Only "safe to share" fields are editable in batch. Per-file-unique fields
/// (Title, EpisodeTitle, Season, Episode, Description, AiredDate, *Id, Rating/Votes)
/// are intentionally NOT exposed here — forcing one value across many files would
/// corrupt them.
/// </summary>
public static class BatchFieldEditService
{
    /// <summary>How a multi-value text field (Genre, Cast) is combined with existing data.</summary>
    public enum TextMode { Replace, Append }

    /// <summary>Tri-state for the watched flag.</summary>
    public enum WatchedChange { Leave, SetWatched, SetUnwatched }

    /// <summary>
    /// Describes which fields to change and to what. A null value means "don't touch
    /// this field". This is how the checkbox dialog communicates intent: ticking a box
    /// sets the corresponding value; leaving it unticked leaves it null.
    /// </summary>
    public sealed class BatchFieldEdits
    {
        // Single-value (replace) fields — null = leave unchanged.
        public string? Year      { get; set; }
        public string? Director  { get; set; }
        public string? MpaRating { get; set; }
        public string? ShowTitle { get; set; }

        // Multi-value fields — null = leave unchanged. Mode applies only when non-null.
        public string?  Genre     { get; set; }
        public TextMode GenreMode { get; set; } = TextMode.Replace;
        public string?  Cast      { get; set; }
        public TextMode CastMode  { get; set; } = TextMode.Replace;

        // Watched flag — tri-state.
        public WatchedChange Watched { get; set; } = WatchedChange.Leave;

        /// <summary>True if at least one field is set to change. Used to disable "Apply" on an empty edit.</summary>
        public bool HasAnyChange =>
            Year != null || Director != null || MpaRating != null || ShowTitle != null ||
            Genre != null || Cast != null || Watched != WatchedChange.Leave;
    }

    /// <summary>
    /// Applies <paramref name="edits"/> to <paramref name="target"/> in place. Only opted-in
    /// fields are modified. Append mode merges comma-separated values, de-duplicated
    /// case-insensitively, preserving the existing order and appending genuinely new entries.
    /// </summary>
    public static void ApplyEdits(MovieMetadata target, BatchFieldEdits edits)
    {
        if (target is null || edits is null) return;

        if (edits.Year      != null) target.Year      = edits.Year;
        if (edits.Director  != null) target.Director  = edits.Director;
        if (edits.MpaRating != null) target.MpaRating = edits.MpaRating;
        if (edits.ShowTitle != null) target.ShowTitle = edits.ShowTitle;

        if (edits.Genre != null)
            target.Genre = CombineCsv(target.Genre, edits.Genre, edits.GenreMode);

        if (edits.Cast != null)
            target.Cast = CombineCsv(target.Cast, edits.Cast, edits.CastMode);

        switch (edits.Watched)
        {
            case WatchedChange.SetWatched:   target.IsWatched = true;  break;
            case WatchedChange.SetUnwatched: target.IsWatched = false; break;
        }
    }

    /// <summary>
    /// Combines a comma-separated field with new value(s) according to <paramref name="mode"/>.
    /// Replace → the new value verbatim (normalised). Append → existing entries plus any
    /// new entries not already present (case-insensitive), joined with ", ".
    /// </summary>
    public static string CombineCsv(string? existing, string incoming, TextMode mode)
    {
        if (mode == TextMode.Replace)
            return NormaliseCsv(incoming);

        // Append
        var result = new List<string>();
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in SplitCsv(existing))
            if (seen.Add(item)) result.Add(item);

        foreach (var item in SplitCsv(incoming))
            if (seen.Add(item)) result.Add(item);

        return string.Join(", ", result);
    }

    /// <summary>Trims, drops empties, and re-joins with a consistent ", " separator.</summary>
    private static string NormaliseCsv(string csv)
        => string.Join(", ", SplitCsv(csv));

    private static IEnumerable<string> SplitCsv(string? csv)
        => string.IsNullOrWhiteSpace(csv)
            ? Enumerable.Empty<string>()
            : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
