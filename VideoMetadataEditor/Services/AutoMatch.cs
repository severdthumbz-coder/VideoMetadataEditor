using System.Text.RegularExpressions;
using VideoMetadataEditor.Models;

namespace VideoMetadataEditor.Services;

/// <summary>
/// Decides what auto-embed should do with a search result list (build 178): embed the
/// best match straight away, put the file in the review queue with the candidates, or
/// leave it for manual search.
///
/// Before 178, auto-embed scored titles only, so two TMDB entries with the same title and
/// year (e.g. two "One Last Shot (2026)" listings) both scored 100% and it took whichever
/// TMDB listed first. Now anything ambiguous goes to the review queue instead.
/// WPF-free so every rule is unit-tested.
/// </summary>
public static class AutoMatch
{
    public enum Kind { Auto, Review, NoMatch }

    public sealed record Decision(Kind Kind, SearchResult? Best, IReadOnlyList<SearchResult> Candidates, string Reason);

    public const int MaxCandidates = 8;

    /// <summary>Score below which a result isn't worth showing as a candidate at all.</summary>
    public const double CandidateFloor = 0.40;

    private static readonly double[] Thresholds = { 0.40, 0.60, 0.80, 1.00 };

    /// <summary>Settings level (0 Low … 3 Exact) → required title similarity.</summary>
    public static double ThresholdFor(int level) => Thresholds[Math.Clamp(level, 0, 3)];

    // ── Already identified? ──────────────────────────────────────────────────

    /// <summary>
    /// True when the file already carries a database ID (TMDB / IMDb / series), i.e. it was
    /// identified by VME or another proper tagger. A bare title and year — what release
    /// groups put on their MP4s — does NOT count: those files still need matching.
    /// </summary>
    public static bool AlreadyIdentified(MovieMetadata? m) =>
        m != null && (!string.IsNullOrWhiteSpace(m.TmdbId) ||
                      !string.IsNullOrWhiteSpace(m.ImdbId) ||
                      !string.IsNullOrWhiteSpace(m.TmdbSeriesId));

    // ── Title comparison (shared with the rest of the app) ───────────────────

    /// <summary>Lower-case, apostrophes removed, punctuation to spaces, trailing "(2020)" dropped.</summary>
    public static string NormaliseTitle(string? title)
    {
        var t = (title ?? string.Empty).ToLowerInvariant();
        t = Regex.Replace(t, @"\s*\(\d{4}\)\s*$", "");
        // Apostrophes are REMOVED so "Marvel's" and "Marvels" compare equal.
        t = Regex.Replace(t, "['‘’`]", "");
        t = Regex.Replace(t, @"[^a-z0-9\s]", " ");
        t = Regex.Replace(t, @"\s{2,}", " ").Trim();
        return t;
    }

    /// <summary>0–1 similarity of two normalised titles (exact 1.0, containment 0.9, else word overlap).</summary>
    public static double Similarity(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0;
        if (a == b) return 1.0;
        if (a.Contains(b) || b.Contains(a)) return 0.9;
        var wa = new HashSet<string>(a.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        var wb = new HashSet<string>(b.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        int common = wa.Intersect(wb).Count();
        int total  = wa.Union(wb).Count();
        return total == 0 ? 0 : (double)common / total;
    }

    public static double Score(string candidateTitle, string query) =>
        Similarity(NormaliseTitle(candidateTitle), NormaliseTitle(query));

    // ── Movies ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Movie decision. Auto only when exactly one result clearly fits: it passes the
    /// confidence threshold, its year agrees with the filename (or the title match is
    /// near-exact), and no other result has the same title with the same — or, when the
    /// filename has no year, any — year. <paramref name="alwaysReview"/> sends every
    /// match to the queue (Settings: "Always let me choose movies").
    /// </summary>
    public static Decision DecideMovie(IReadOnlyList<SearchResult> results, string query, string? year,
                                       double threshold, bool alwaysReview = false)
    {
        if (results.Count == 0) return new(Kind.NoMatch, null, Array.Empty<SearchResult>(), "no search results");

        bool hasYear = !string.IsNullOrWhiteSpace(year);
        var scored = results
            .Select((r, i) => (r, i, score: Score(r.Title, query), yearMatch: hasYear && r.Year == year))
            .OrderByDescending(x => x.score + (x.yearMatch ? 0.3 : 0))
            .ThenByDescending(x => x.r.Popularity)
            .ThenBy(x => x.i)
            .ToList();

        var candidates = scored.Where(x => x.score >= CandidateFloor).Take(MaxCandidates).Select(x => x.r).ToList();
        var top = scored[0];
        bool passes = top.score >= threshold;
        // A near-exact title may differ by a year (festival vs. release date). More than
        // that is probably a different film with the same name (remakes) — review it.
        bool yearOk = !hasYear || top.yearMatch || (top.score >= 0.80 && YearsApart(top.r.Year, year) <= 1);

        if (!passes || !yearOk)
        {
            var why = !passes
                ? $"best match \"{top.r.Title}\" ({top.r.Year}) is only a {top.score:P0} title match"
                : $"best match \"{top.r.Title}\" is from {Blank(top.r.Year)}, the filename says {year}";
            return candidates.Count > 0
                ? new(Kind.Review, top.r, candidates, why)
                : new(Kind.NoMatch, null, candidates, why);
        }

        // Same title as the best match (after normalising) …
        var topNorm = NormaliseTitle(top.r.Title);
        var twins = scored.Where(x => x.r != top.r && NormaliseTitle(x.r.Title) == topNorm).ToList();
        // … with the same year — or any year when the filename doesn't say which.
        var clashing = hasYear ? twins.Where(x => x.r.Year == top.r.Year).ToList() : twins;
        if (clashing.Count > 0)
        {
            var reason = hasYear
                ? $"{clashing.Count + 1} entries are called \"{top.r.Title}\" ({top.r.Year})"
                : $"{clashing.Count + 1} films are called \"{top.r.Title}\" and the filename has no year " +
                  $"({string.Join(", ", new[] { top }.Concat(clashing).Select(x => Blank(x.r.Year)).Distinct())})";
            return new(Kind.Review, top.r, candidates, reason);
        }

        if (alwaysReview)
            return new(Kind.Review, top.r, candidates, "you've chosen to pick movie matches yourself");

        return new(Kind.Auto, top.r, candidates, $"\"{top.r.Title}\" ({top.r.Year}) — {top.score:P0} title match" +
                                                  (top.yearMatch ? ", year matches" : ""));
    }

    // ── TV shows ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Show decision. Auto when one show clearly fits: an exact title match, or the single
    /// best match above the threshold — and no second show with the same title (remakes
    /// and reboots, e.g. "Doctor Who" 1963 and 2005).
    /// </summary>
    public static Decision DecideShow(IReadOnlyList<SearchResult> results, string query, double threshold,
                                      IReadOnlyCollection<string>? knownSeriesIds = null)
    {
        if (results.Count == 0) return new(Kind.NoMatch, null, Array.Empty<SearchResult>(), "no search results");

        var qNorm = NormaliseTitle(query);
        var scored = results
            .Select((r, i) => (r, i, score: Similarity(NormaliseTitle(r.Title), qNorm)))
            .OrderByDescending(x => x.score).ThenByDescending(x => x.r.Popularity).ThenBy(x => x.i)
            .ToList();
        var candidates = scored.Where(x => x.score >= CandidateFloor).Take(MaxCandidates).Select(x => x.r).ToList();
        var top = scored[0];

        // The library already has episodes of one of these shows — that settles which one,
        // even when two shows share the title.
        if (knownSeriesIds is { Count: > 0 })
        {
            var known = scored.FirstOrDefault(x => x.score >= CandidateFloor && knownSeriesIds.Contains(x.r.TmdbId));
            if (known.r != null)
                return new(Kind.Auto, known.r, candidates,
                    $"\"{known.r.Title}\" ({Blank(known.r.Year)}) — the same show as your other episodes");
        }

        if (top.score < Math.Max(threshold, 0.70))
        {
            var why = $"best match \"{top.r.Title}\" is only a {top.score:P0} title match";
            return candidates.Count > 0 ? new(Kind.Review, top.r, candidates, why) : new(Kind.NoMatch, null, candidates, why);
        }

        var topNorm = NormaliseTitle(top.r.Title);
        var twins = scored.Where(x => x.r != top.r && NormaliseTitle(x.r.Title) == topNorm).ToList();
        if (twins.Count > 0)
            return new(Kind.Review, top.r, candidates,
                $"{twins.Count + 1} shows are called \"{top.r.Title}\" " +
                $"({string.Join(", ", new[] { top }.Concat(twins).Select(x => Blank(x.r.Year)))})");

        return new(Kind.Auto, top.r, candidates, $"\"{top.r.Title}\" ({top.r.Year}) — {top.score:P0} title match");
    }

    private static int YearsApart(string? a, string? b) =>
        int.TryParse(a, out var x) && int.TryParse(b, out var y) ? Math.Abs(x - y) : int.MaxValue;

    private static string Blank(string? year) => string.IsNullOrWhiteSpace(year) ? "year unknown" : year;

    /// <summary>Candidate list for the log: one line each.</summary>
    public static string DescribeCandidates(IEnumerable<SearchResult> candidates) =>
        string.Join(Environment.NewLine, candidates.Select(c =>
            $"{c.Title} ({Blank(c.Year)}) — TMDB {c.TmdbId}" + (string.IsNullOrEmpty(c.ImdbId) ? "" : $", {c.ImdbId}")));
}
