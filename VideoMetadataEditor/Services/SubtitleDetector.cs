using System.IO;
using VideoMetadataEditor.Models;

namespace VideoMetadataEditor.Services;

/// <summary>
/// Detects subtitle sidecar files for a given video path.
///
/// Naming conventions supported:
///   Video.srt                → language unknown
///   Video.en.srt             → English
///   Video.en.forced.srt      → English forced
///   Video.en.hi.srt          → English hearing-impaired
///   Video.fr.sdh.srt         → French SDH (same as HI)
///   Video.sub + Video.idx    → VobSub pair
///
/// Language codes: 2-letter ISO 639-1 (en, fr, de) or 3-letter ISO 639-2 (eng, fra).
/// </summary>
public static class SubtitleDetector
{
    private static readonly HashSet<string> SubtitleExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".srt", ".ass", ".ssa", ".vtt", ".sub", ".idx", ".sbv", ".lrc"
        };

    private static readonly Dictionary<string, string> Iso639 =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // 2-letter
            {"en","English"},  {"fr","French"},   {"de","German"},   {"es","Spanish"},
            {"it","Italian"},  {"pt","Portuguese"},{"ja","Japanese"}, {"ko","Korean"},
            {"zh","Chinese"},  {"ru","Russian"},   {"ar","Arabic"},   {"nl","Dutch"},
            {"sv","Swedish"},  {"da","Danish"},    {"fi","Finnish"},  {"no","Norwegian"},
            {"pl","Polish"},   {"cs","Czech"},     {"sk","Slovak"},   {"hu","Hungarian"},
            {"ro","Romanian"}, {"tr","Turkish"},   {"he","Hebrew"},   {"hi","Hindi"},
            {"th","Thai"},     {"vi","Vietnamese"},{"id","Indonesian"},{"ms","Malay"},
            {"uk","Ukrainian"},{"bg","Bulgarian"}, {"hr","Croatian"}, {"sr","Serbian"},
            {"ca","Catalan"},  {"el","Greek"},     {"fa","Persian"},  {"lt","Lithuanian"},
            {"lv","Latvian"},  {"et","Estonian"},  {"sl","Slovenian"},{"mk","Macedonian"},
            // 3-letter
            {"eng","English"}, {"fra","French"},   {"deu","German"},  {"spa","Spanish"},
            {"ita","Italian"}, {"por","Portuguese"},{"jpn","Japanese"},{"kor","Korean"},
            {"zho","Chinese"}, {"rus","Russian"},  {"ara","Arabic"},  {"nld","Dutch"},
            {"swe","Swedish"}, {"dan","Danish"},   {"fin","Finnish"}, {"nor","Norwegian"},
            {"pol","Polish"},  {"ces","Czech"},    {"slk","Slovak"},  {"hun","Hungarian"},
            {"ron","Romanian"},{"tur","Turkish"},  {"heb","Hebrew"},  {"hin","Hindi"},
            {"tha","Thai"},    {"vie","Vietnamese"},{"ind","Indonesian"},
        };

    public static List<SubtitleFile> Detect(string videoPath)
    {
        var results = new List<SubtitleFile>();
        try
        {
            var dir      = Path.GetDirectoryName(videoPath);
            var baseName = Path.GetFileNameWithoutExtension(videoPath);
            if (string.IsNullOrWhiteSpace(dir)) return results;

            // Enumerate all subtitle files in the same folder
            foreach (var file in Directory.EnumerateFiles(dir))
            {
                var ext = Path.GetExtension(file);
                if (!SubtitleExtensions.Contains(ext)) continue;

                var subBase = Path.GetFileNameWithoutExtension(file);

                // Must start with the video base name
                if (!subBase.StartsWith(baseName, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Parse the suffix after the base name: e.g. ".en.forced"
                var suffix = subBase.Length > baseName.Length
                    ? subBase[baseName.Length..].TrimStart('.')
                    : string.Empty;

                var sub = new SubtitleFile
                {
                    FilePath      = file,
                    Format        = ParseFormat(ext),
                    FileSizeBytes = new FileInfo(file).Length,
                };

                // Parse suffix tokens: en, forced, hi, sdh, cc
                if (!string.IsNullOrWhiteSpace(suffix))
                {
                    var tokens = suffix.Split('.', '-', '_');
                    foreach (var token in tokens)
                    {
                        if (token.Equals("forced", StringComparison.OrdinalIgnoreCase))
                        { sub.IsForced = true; continue; }
                        if (token.Equals("hi", StringComparison.OrdinalIgnoreCase)
                         || token.Equals("sdh", StringComparison.OrdinalIgnoreCase)
                         || token.Equals("cc", StringComparison.OrdinalIgnoreCase))
                        { sub.IsHearingImpaired = true; continue; }
                        if (Iso639.TryGetValue(token, out var langName))
                        {
                            sub.LanguageCode    = token.Length == 3
                                ? Iso639ToTwoLetter(token) : token;
                            sub.LanguageDisplay = langName;
                        }
                    }
                }

                results.Add(sub);
            }
        }
        catch { /* directory unreadable */ }

        // Sort: by language, then format
        return results.OrderBy(s => s.LanguageDisplay)
                      .ThenBy(s => s.FormatDisplay)
                      .ToList();
    }

    private static SubtitleFormat ParseFormat(string ext) => ext.ToLowerInvariant() switch
    {
        ".srt" => SubtitleFormat.SRT,
        ".ass" => SubtitleFormat.ASS,
        ".ssa" => SubtitleFormat.SSA,
        ".vtt" => SubtitleFormat.VTT,
        ".sub" => SubtitleFormat.SUB,
        ".idx" => SubtitleFormat.IDX,
        ".sbv" => SubtitleFormat.SBV,
        ".lrc" => SubtitleFormat.LRC,
        _      => SubtitleFormat.Unknown,
    };

    private static string Iso639ToTwoLetter(string threeLetterCode)
    {
        // Map 3-letter to 2-letter for normalisation
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            {"eng","en"},{"fra","fr"},{"deu","de"},{"spa","es"},{"ita","it"},
            {"por","pt"},{"jpn","ja"},{"kor","ko"},{"zho","zh"},{"rus","ru"},
            {"ara","ar"},{"nld","nl"},{"swe","sv"},{"dan","da"},{"fin","fi"},
            {"nor","no"},{"pol","pl"},{"ces","cs"},{"slk","sk"},{"hun","hu"},
            {"ron","ro"},{"tur","tr"},{"heb","he"},{"hin","hi"},{"tha","th"},
            {"vie","vi"},{"ind","id"},
        };
        return map.TryGetValue(threeLetterCode, out var two) ? two : threeLetterCode;
    }

    /// <summary>
    /// Returns a compact display string for a list of subtitle files.
    /// e.g. "EN, FR, DE (SRT)" or "2 subtitles" or "EN (SRT, ASS)"
    /// </summary>
    public static string BuildSummary(IReadOnlyList<SubtitleFile> subs)
    {
        if (subs == null || subs.Count == 0) return string.Empty;

        var langs  = subs.Where(s => !string.IsNullOrWhiteSpace(s.LanguageDisplay))
                         .Select(s => s.LanguageCode.ToUpperInvariant())
                         .Distinct().OrderBy(x => x).ToList();
        var fmts   = subs.Select(s => s.FormatDisplay).Distinct().OrderBy(x => x).ToList();
        var forced = subs.Any(s => s.IsForced) ? " [F]" : string.Empty;
        var hi     = subs.Any(s => s.IsHearingImpaired) ? " [HI]" : string.Empty;

        var langPart = langs.Count > 0 ? string.Join(", ", langs) : $"{subs.Count} sub(s)";
        var fmtPart  = fmts.Count > 0  ? $" ({string.Join("/", fmts)})" : string.Empty;
        return $"{langPart}{fmtPart}{forced}{hi}";
    }
}
