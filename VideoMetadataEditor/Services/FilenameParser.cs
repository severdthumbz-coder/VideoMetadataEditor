using System.IO;
using System.Text.RegularExpressions;

namespace VideoMetadataEditor.Services;

public static class FilenameParser
{
    // ── TV episode code patterns ──────────────────────────────────────────────
    // Matches: S01E01, s1e1, 1x01, 1X01, Season 1 Episode 1
    private static readonly Regex EpisodeRegex = new(
        @"(?i)(?:S(\d{1,2})E(\d{1,3})|(\d{1,2})[xX](\d{2,3})|[Ss]eason\s*(\d{1,2})\s*[Ee]pisode\s*(\d{1,2}))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // ── Known noise tokens to strip after the year ────────────────────────────
    private static readonly string[] NoiseTokens =
    [
        "directors cut", "director's cut", "extended cut", "theatrical cut",
        "unrated", "remastered", "repack", "proper",
        "2160p", "1080p", "1080i", "720p", "720i", "576p", "480p",
        "bluray", "blu-ray", "bdrip", "brrip", "bdremux",
        "webrip", "web-rip", "web-dl", "webdl", "hdrip", "hdtv", "dvdrip", "dvdscr",
        "x264", "x265", "h264", "h265", "hevc", "avc", "xvid", "divx",
        "aac", "ac3", "dts", "truehd", "flac", "mp3", "dd5.1", "dd2.0",
        "10bit", "8bit", "hdr", "hdr10", "dv", "dolby vision",
        "esub", "hsubs", "subs", "multi", "dual audio",
        "hollymoviehd", "yify", "yts", "rarbg", "fgt", "mkvcage", "galadriel",
        "extended", "theatrical", "limited", "internal", "remux"
    ];

    // ── Regex: year is a 4-digit number between 1900-2099 ────────────────────
    private static readonly Regex YearRegex = new(
        @"(?<![0-9])(?:^|[\s.\-_(\\[])(?<year>19[0-9]{2}|20[0-9]{2})(?:$|[\s.\-_)\]\\[p])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ── Separators that act like spaces in dot/underscore-separated names ─────
    private static readonly Regex SeparatorRegex = new(@"[._]+", RegexOptions.Compiled);

    // ── Multiple spaces collapse ───────────────────────────────────────────────
    private static readonly Regex MultiSpace = new(@"\s{2,}", RegexOptions.Compiled);

    /// <summary>
    /// Parses a video filename and returns (title, year).
    /// Handles formats like:
    ///   Commando.1985.Directors.Cut.1080p.BluRay.x264-ESub.Hollymoviehd
    ///   Anna.2019.720p.BluRay.x264-ESub.Hollymoviehd
    ///   The Wonderful Story of Henry Sugar (2023)
    ///   The.Dark.Knight.2008.1080p.BluRay
    ///   Movie Title - 2020 - Extra Info
    /// </summary>
    public static (string title, string year) Parse(string fileNameWithoutExtension)
    {
        var input = fileNameWithoutExtension;

        // 1. Strip leading/trailing brackets/parens that wrap the whole name
        input = input.Trim('[', ']', '(', ')', ' ');

        // 2. Replace dots and underscores with spaces (common in scene releases)
        //    BUT only if the name looks dot/underscore-separated (not a normal spaced name)
        var normalized = NormalizeSeparators(input);

        // 2b. Strip episode code and everything after it from the normalized string
        //     so episode codes don't pollute the title extracted for movies/TV searches.
        //     e.g. "Dr Stone S01E02 720p x264" → "Dr Stone"
        var epCut = EpisodeRegex.Match(normalized);
        if (epCut.Success)
            normalized = normalized.Substring(0, epCut.Index).Trim();

        // 3. Find the year — it's the anchor between title and technical tags
        var yearMatch = FindYear(normalized);

        if (yearMatch != null)
        {
            var (yearValue, yearIndex) = yearMatch.Value;
            var year      = yearValue.Trim(' ', '.', '-', '_', '(', ')');
            var titlePart = normalized.Substring(0, yearIndex).Trim();
            var title     = CleanTitle(titlePart);
            return (title, year);
        }

        // 4. No year found — try stripping noise tokens from the end to get a title
        var titleOnly = StripTrailingNoise(normalized);
        return (CleanTitle(titleOnly), string.Empty);
    }

    // ── Normalize separators ─────────────────────────────────────────────────

    private static string NormalizeSeparators(string input)
    {
        // Detect if it's a dot/underscore-separated scene filename
        // Heuristic: more dots than spaces → scene filename
        int dots   = input.Count(c => c == '.');
        int spaces = input.Count(c => c == ' ');

        if (dots > spaces)
        {
            // Replace dots/underscores between words with spaces
            // But keep decimal-looking things and year patterns intact
            input = SeparatorRegex.Replace(input, " ");
        }
        else if (input.Contains('_') && spaces < 2)
        {
            input = input.Replace('_', ' ');
        }

        // Normalize dashes used as separators (e.g. "Title - 2020 - Extra")
        input = Regex.Replace(input, @"\s-\s", " - ");

        return MultiSpace.Replace(input, " ").Trim();
    }

    // ── Find year match ───────────────────────────────────────────────────────

    private static (string year, int index)? FindYear(string input)
    {
        var matches = YearRegex.Matches(input);
        if (matches.Count == 0) return null;

        foreach (Match m in matches)
        {
            var yearGroup = m.Groups["year"];
            if (yearGroup.Success)
                return (yearGroup.Value, yearGroup.Index);
        }
        return null;
    }

    // ── Clean title string ────────────────────────────────────────────────────

    private static string CleanTitle(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return raw;

        // Remove trailing dashes/separators AND unmatched opening brackets
        // e.g. "100% Wolf (" left after extracting year from "(2020)"
        raw = raw.TrimEnd(' ', '-', '.', '_', '–', '—', '(', '[');

        // Remove common suffix noise that can appear before year without proper separator
        var lower = raw.ToLowerInvariant();
        foreach (var noise in NoiseTokens)
        {
            if (lower.EndsWith(noise))
            {
                raw   = raw[..^noise.Length].TrimEnd(' ', '-', '.', '_', '(', '[');
                lower = raw.ToLowerInvariant();
            }
        }

        // Remove stray brackets/parens at start or end
        raw = Regex.Replace(raw, @"^\s*[\[\(]\s*|\s*[\]\)]\s*$", "").Trim();

        // Trim again in case bracket removal left trailing separators
        raw = raw.TrimEnd(' ', '-', '.', '_');

        // Title-case: only apply if all-caps or all-lower (preserve intentional mixed case)
        if (raw == raw.ToUpperInvariant() || raw == raw.ToLowerInvariant())
            raw = ToTitleCase(raw);

        return MultiSpace.Replace(raw, " ").Trim();
    }

    // ── Strip trailing noise when no year is found ────────────────────────────

    private static string StripTrailingNoise(string input)
    {
        var parts = input.Split(' ');
        var result = new List<string>();

        foreach (var part in parts)
        {
            var lower = part.ToLowerInvariant().TrimEnd('-');
            if (NoiseTokens.Any(n => lower == n || lower.StartsWith(n)))
                break; // stop at first noise token
            result.Add(part);
        }

        return result.Count > 0 ? string.Join(" ", result) : input;
    }

    // ── Simple title case ─────────────────────────────────────────────────────

    private static readonly HashSet<string> LowercaseWords =
        new(["a", "an", "the", "and", "but", "or", "for", "nor",
              "on", "at", "to", "by", "in", "of", "up", "as", "is"],
            StringComparer.OrdinalIgnoreCase);

    private static string ToTitleCase(string input)
    {
        var words = input.Split(' ');
        for (int i = 0; i < words.Length; i++)
        {
            var w = words[i];
            if (string.IsNullOrEmpty(w)) continue;
            // Always capitalize first and last word; lowercase articles in between
            if (i == 0 || i == words.Length - 1 || !LowercaseWords.Contains(w))
                words[i] = char.ToUpperInvariant(w[0]) + (w.Length > 1 ? w[1..].ToLowerInvariant() : "");
            else
                words[i] = w.ToLowerInvariant();
        }
        return string.Join(" ", words);
    }

    // ── Build search query from parsed result ──────────────────────────────────

    /// <summary>
    /// Attempts to parse a TV episode filename.
    /// Returns (showTitle, season, episode) if an episode pattern is found; null otherwise.
    /// Show title is everything before the episode code.
    /// Example: "Batman.The.Animated.Series.S01E05.1080p" → ("Batman The Animated Series", 1, 5)
    /// </summary>
    public static (string showTitle, int season, int episode)? ParseEpisode(
        string fileNameWithoutExtension)
    {
        var input = fileNameWithoutExtension;
        var m = EpisodeRegex.Match(input);
        if (!m.Success) return null;

        // Extract season + episode from whichever capture group matched
        int season, episode;
        if (m.Groups[1].Success)        // S01E01
        {
            season  = int.Parse(m.Groups[1].Value);
            episode = int.Parse(m.Groups[2].Value);
        }
        else if (m.Groups[3].Success)   // 1x01
        {
            season  = int.Parse(m.Groups[3].Value);
            episode = int.Parse(m.Groups[4].Value);
        }
        else                             // Season 1 Episode 1
        {
            season  = int.Parse(m.Groups[5].Value);
            episode = int.Parse(m.Groups[6].Value);
        }

        // Show title = everything before the episode code
        var before = input[..m.Index];
        var normalized = NormalizeSeparators(before);
        var showTitle  = CleanTitle(normalized);

        if (string.IsNullOrWhiteSpace(showTitle)) return null;
        return (showTitle, season, episode);
    }

    // ── Multi-episode range patterns ──────────────────────────────────────────
    // Matches the END episode of a range. Tried in priority order:
    //   S01E01-E03 / S01E01-03 / s1e1-e3   (dash range, optional E on the end)
    //   1x01-1x03  / 1x01-03                (cross range, optional NxNN on the end)
    //   S01E01E02E03 / E01E02E03            (consecutive Exx run — last number wins)
    //   01_02 / 01-02                       (bare two-number pair, e.g. "Show 01_02")
    private static readonly Regex RangeDashRegex = new(
        @"(?i)S\d{1,2}E\d{1,3}\s*-\s*(?:S\d{1,2})?E?(\d{1,3})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex RangeCrossRegex = new(
        @"(?i)(\d{1,2})[xX](\d{2,3})\s*-\s*(?:\d{1,2}[xX])?(\d{2,3})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex RangeConsecutiveRegex = new(
        @"(?i)S\d{1,2}(E\d{1,3}){2,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ConsecutiveEpRegex = new(
        @"(?i)E(\d{1,3})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex RangeBareRegex = new(
        @"(?<![\dxXeE])(\d{1,3})[_\-](\d{1,3})(?![\dxX])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Detects the END episode number of a multi-episode range in a source filename.
    /// Returns the end episode (e.g. 3 for "S01E01-E03") only when it is strictly
    /// greater than the start episode parsed by <see cref="ParseEpisode"/>; otherwise
    /// returns null (no range, malformed range, or end ≤ start).
    /// Pure function — used only when the DetectEpisodeRange setting is enabled.
    /// </summary>
    public static int? ParseEpisodeRange(string fileNameWithoutExtension)
    {
        var input = fileNameWithoutExtension ?? string.Empty;

        // We need the start episode to validate that end > start.
        var startParse = ParseEpisode(input);
        if (startParse is null) return null;
        int startEpisode = startParse.Value.episode;

        // 1. S01E01-E03 style (most explicit; check first)
        var dash = RangeDashRegex.Match(input);
        if (dash.Success && int.TryParse(dash.Groups[1].Value, out int dashEnd))
            return dashEnd > startEpisode ? dashEnd : (int?)null;

        // 2. 1x01-1x03 style
        var cross = RangeCrossRegex.Match(input);
        if (cross.Success && int.TryParse(cross.Groups[3].Value, out int crossEnd))
            return crossEnd > startEpisode ? crossEnd : (int?)null;

        // 3. S01E01E02E03 consecutive run — last Exx wins
        var consec = RangeConsecutiveRegex.Match(input);
        if (consec.Success)
        {
            var eps = ConsecutiveEpRegex.Matches(consec.Value);
            if (eps.Count >= 2 &&
                int.TryParse(eps[^1].Groups[1].Value, out int consecEnd))
                return consecEnd > startEpisode ? consecEnd : (int?)null;
        }

        // 4. Bare "01_02" / "01-02" pair (only when start episode appears as the
        //    first number, to avoid matching years/resolutions/etc.)
        foreach (Match bare in RangeBareRegex.Matches(input))
        {
            if (int.TryParse(bare.Groups[1].Value, out int bareStart) &&
                int.TryParse(bare.Groups[2].Value, out int bareEnd) &&
                bareStart == startEpisode && bareEnd > bareStart)
                return bareEnd;
        }

        return null;
    }

    /// <summary>
    /// Returns the best search query string: "Title Year" if year known, else just "Title".
    /// </summary>
    public static string BuildSearchQuery(string title, string year)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;
        return string.IsNullOrWhiteSpace(year) ? title : $"{title} {year}";
    }

    // ── Test helper (used in unit tests / debug) ───────────────────────────────

    public static string Describe(string filename)
    {
        var ext  = Path.GetExtension(filename);
        var name = Path.GetFileNameWithoutExtension(filename);
        var (title, year) = Parse(name);
        return $"File: {filename}\n  → Title: {title}\n  → Year:  {(string.IsNullOrEmpty(year) ? "(not found)" : year)}\n  → Query: {BuildSearchQuery(title, year)}";
    }
}
