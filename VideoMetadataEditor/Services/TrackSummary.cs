namespace VideoMetadataEditor.Services;

/// <summary>
/// Audio and subtitle tracks inside a video, for the Library's track columns and
/// filters (build 175).
///
/// The track list is stored on each library entry (and in the scan cache) as one compact
/// string, e.g. "a:en:aac|a:ja:aac|s:en:subrip:f", so a cached library doesn't re-probe
/// every file. null means "not read yet"; an empty string means "read, no audio or
/// subtitle tracks". Languages are normalised to two-letter codes where known, so
/// "eng" (MP4), "eng" (MKV) and sidecar "en" all match the same filter.
///
/// WPF-free so the encoding, display and filter rules are unit-tested.
/// </summary>
public static class TrackSummary
{
    public sealed record Track(bool IsAudio, string Lang, string Codec, bool Forced);

    public sealed record FilterOption(string Key, string Label);

    public const string Any = "any";

    // ── Languages ─────────────────────────────────────────────────────────────

    // ISO 639-2 (both T and B forms — MKV often uses B: "fre", "ger", "chi") → 639-1.
    private static readonly Dictionary<string, string> ThreeToTwo = new(StringComparer.OrdinalIgnoreCase)
    {
        {"eng","en"},{"fra","fr"},{"fre","fr"},{"deu","de"},{"ger","de"},{"spa","es"},{"ita","it"},
        {"por","pt"},{"jpn","ja"},{"kor","ko"},{"zho","zh"},{"chi","zh"},{"cmn","zh"},{"yue","zh"},
        {"rus","ru"},{"ara","ar"},{"nld","nl"},{"dut","nl"},{"swe","sv"},{"dan","da"},{"fin","fi"},
        {"nor","no"},{"nob","no"},{"nno","no"},{"pol","pl"},{"ces","cs"},{"cze","cs"},{"slk","sk"},
        {"slo","sk"},{"hun","hu"},{"ron","ro"},{"rum","ro"},{"tur","tr"},{"heb","he"},{"hin","hi"},
        {"tha","th"},{"vie","vi"},{"ind","id"},{"msa","ms"},{"may","ms"},{"ukr","uk"},{"bul","bg"},
        {"hrv","hr"},{"srp","sr"},{"cat","ca"},{"ell","el"},{"gre","el"},{"fas","fa"},{"per","fa"},
        {"lit","lt"},{"lav","lv"},{"est","et"},{"slv","sl"},{"mkd","mk"},{"mac","mk"},{"isl","is"},
        {"ice","is"},{"tam","ta"},{"tel","te"},{"tgl","tl"},{"fil","tl"},{"ben","bn"},{"urd","ur"},
        {"glg","gl"},{"eus","eu"},{"baq","eu"},{"lat","la"},{"kan","kn"},{"mal","ml"},{"mar","mr"},
        {"pan","pa"},{"guj","gu"},{"bos","bs"},{"alb","sq"},{"sqi","sq"},{"arm","hy"},{"hye","hy"},
        {"geo","ka"},{"kat","ka"},{"swa","sw"},{"afr","af"},{"wel","cy"},{"cym","cy"},{"gle","ga"},
    };

    private static readonly Dictionary<string, string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        {"en","English"},{"fr","French"},{"de","German"},{"es","Spanish"},{"it","Italian"},
        {"pt","Portuguese"},{"ja","Japanese"},{"ko","Korean"},{"zh","Chinese"},{"ru","Russian"},
        {"ar","Arabic"},{"nl","Dutch"},{"sv","Swedish"},{"da","Danish"},{"fi","Finnish"},
        {"no","Norwegian"},{"pl","Polish"},{"cs","Czech"},{"sk","Slovak"},{"hu","Hungarian"},
        {"ro","Romanian"},{"tr","Turkish"},{"he","Hebrew"},{"hi","Hindi"},{"th","Thai"},
        {"vi","Vietnamese"},{"id","Indonesian"},{"ms","Malay"},{"uk","Ukrainian"},{"bg","Bulgarian"},
        {"hr","Croatian"},{"sr","Serbian"},{"ca","Catalan"},{"el","Greek"},{"fa","Persian"},
        {"lt","Lithuanian"},{"lv","Latvian"},{"et","Estonian"},{"sl","Slovenian"},{"mk","Macedonian"},
        {"is","Icelandic"},{"ta","Tamil"},{"te","Telugu"},{"tl","Filipino"},{"bn","Bengali"},
        {"ur","Urdu"},{"gl","Galician"},{"eu","Basque"},{"la","Latin"},{"kn","Kannada"},
        {"ml","Malayalam"},{"mr","Marathi"},{"pa","Punjabi"},{"gu","Gujarati"},{"bs","Bosnian"},
        {"sq","Albanian"},{"hy","Armenian"},{"ka","Georgian"},{"sw","Swahili"},{"af","Afrikaans"},
        {"cy","Welsh"},{"ga","Irish"},
    };

    /// <summary>"eng", "ENG", "en", "en-US" → "en"; empty / "und" → "und".</summary>
    public static string NormalizeLang(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "und";
        var c = code.Trim().ToLowerInvariant();
        var dash = c.IndexOfAny(new[] { '-', '_' });
        if (dash > 0) c = c[..dash];
        if (c is "und" or "unk" or "zxx" or "mis" or "mul") return "und";
        if (c.Length == 3 && ThreeToTwo.TryGetValue(c, out var two)) return two;
        return c;
    }

    public static string LanguageName(string code)
    {
        var c = NormalizeLang(code);
        if (c == "und") return "Unknown";
        return Names.TryGetValue(c, out var n) ? n : c.ToUpperInvariant();
    }

    // ── Encoding ──────────────────────────────────────────────────────────────

    /// <summary>Encodes the audio and subtitle tracks of a probe (video, cover art and data are left out).</summary>
    public static string Encode(MediaProbeService.ProbeResult probe)
    {
        var parts = new List<string>();
        foreach (var s in probe.Streams)
        {
            if (s.Kind is not (MediaProbeService.StreamKind.Audio or MediaProbeService.StreamKind.Subtitle)) continue;
            var kind  = s.Kind == MediaProbeService.StreamKind.Audio ? "a" : "s";
            var codec = Clean(s.Codec);
            parts.Add(s.Forced ? $"{kind}:{NormalizeLang(s.Language)}:{codec}:f"
                               : $"{kind}:{NormalizeLang(s.Language)}:{codec}");
        }
        return string.Join("|", parts);
    }

    private static string Clean(string? v) =>
        string.IsNullOrWhiteSpace(v) ? "" : v.Trim().ToLowerInvariant().Replace("|", "").Replace(":", "");

    public static List<Track> Decode(string? encoded)
    {
        var list = new List<Track>();
        if (string.IsNullOrEmpty(encoded)) return list;
        foreach (var part in encoded.Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = part.Split(':');
            if (f.Length < 2 || (f[0] != "a" && f[0] != "s")) continue;
            list.Add(new Track(f[0] == "a", NormalizeLang(f[1]), f.Length > 2 ? f[2] : "",
                               f.Length > 3 && f[3] == "f"));
        }
        return list;
    }

    public static int AudioCount(string? encoded)    => Decode(encoded).Count(t => t.IsAudio);
    public static int SubtitleCount(string? encoded) => Decode(encoded).Count(t => !t.IsAudio);

    public static IReadOnlyList<string> AudioLanguages(string? encoded) =>
        Decode(encoded).Where(t => t.IsAudio).Select(t => t.Lang).Distinct().ToList();

    public static IReadOnlyList<string> SubtitleLanguages(string? encoded) =>
        Decode(encoded).Where(t => !t.IsAudio).Select(t => t.Lang).Distinct().ToList();

    // ── Display ──────────────────────────────────────────────────────────────

    private static string Codes(IEnumerable<Track> tracks) =>
        string.Join(", ", tracks.Select(t => t.Lang == "und" ? "?" : t.Lang.ToUpperInvariant()).Distinct());

    /// <summary>"EN" for one track, "2 · EN, JA" for several; empty when not read yet.</summary>
    public static string AudioDisplay(string? encoded)
    {
        if (encoded == null) return string.Empty;
        var a = Decode(encoded).Where(t => t.IsAudio).ToList();
        if (a.Count == 0) return "none";
        return a.Count == 1 ? Codes(a) : $"{a.Count} · {Codes(a)}";
    }

    /// <summary>"3 · EN, ES, FR", plus "[F]" when one is forced; "none" when the file has none.</summary>
    public static string SubtitleDisplay(string? encoded)
    {
        if (encoded == null) return string.Empty;
        var s = Decode(encoded).Where(t => !t.IsAudio).ToList();
        if (s.Count == 0) return "none";
        var text = s.Count == 1 ? Codes(s) : $"{s.Count} · {Codes(s)}";
        return s.Any(t => t.Forced) ? text + " [F]" : text;
    }

    /// <summary>Tooltip text: one line per track with language name, codec and forced flag.</summary>
    public static string Describe(string? encoded)
    {
        if (encoded == null) return "Track info not read yet.";
        var tracks = Decode(encoded);
        if (tracks.Count == 0) return "No audio or subtitle tracks.";
        return string.Join(Environment.NewLine, tracks.Select((t, i) =>
            $"{(t.IsAudio ? "Audio" : "Subtitle")}: {LanguageName(t.Lang)}" +
            (string.IsNullOrEmpty(t.Codec) ? "" : $" ({t.Codec})") + (t.Forced ? " — forced" : "")));
    }

    // ── Filters ──────────────────────────────────────────────────────────────
    // Keys: "any", "lang:xx" (has this language), "multi" (2+ audio tracks),
    // "none" (no subtitles anywhere), "missing:xx" (no subtitle in this language).
    // A file whose tracks haven't been read yet only matches "any" — VME can't say yet.

    public static bool MatchesAudio(string? key, string? encoded)
    {
        if (string.IsNullOrEmpty(key) || key == Any) return true;
        if (encoded == null) return false;
        var audio = Decode(encoded).Where(t => t.IsAudio).ToList();
        if (key == "multi") return audio.Count >= 2;
        if (key.StartsWith("lang:", StringComparison.Ordinal))
            return audio.Any(t => t.Lang == key[5..]);
        return true;
    }

    /// <summary>
    /// Subtitle filter. Counts subtitles inside the file and sidecar files next to it
    /// (<paramref name="sidecarLangs"/>, any language codes; <paramref name="sidecarCount"/>
    /// includes sidecars whose language isn't in the name).
    /// </summary>
    public static bool MatchesSubtitles(string? key, string? encoded,
                                        IEnumerable<string> sidecarLangs, int sidecarCount)
    {
        if (string.IsNullOrEmpty(key) || key == Any) return true;
        var side = sidecarLangs.Select(NormalizeLang).ToHashSet();

        if (key.StartsWith("lang:", StringComparison.Ordinal))
        {
            var lang = key[5..];
            if (side.Contains(lang)) return true;
            return encoded != null && Decode(encoded).Any(t => !t.IsAudio && t.Lang == lang);
        }

        if (encoded == null) return false;   // "none" / "missing" need the file's tracks
        var inFile = Decode(encoded).Where(t => !t.IsAudio).ToList();

        if (key == "none") return inFile.Count == 0 && sidecarCount == 0;
        if (key.StartsWith("missing:", StringComparison.Ordinal))
        {
            var lang = key[8..];
            return !side.Contains(lang) && !inFile.Any(t => t.Lang == lang);
        }
        return true;
    }

    /// <summary>Audio filter choices for the languages actually in the library, most common first.</summary>
    public static List<FilterOption> BuildAudioOptions(IEnumerable<string?> allEncoded)
    {
        var counts = new Dictionary<string, int>();
        int multi = 0;
        foreach (var enc in allEncoded)
        {
            if (enc == null) continue;
            var a = Decode(enc).Where(t => t.IsAudio).ToList();
            if (a.Count >= 2) multi++;
            foreach (var l in a.Select(t => t.Lang).Distinct())
                if (l != "und") counts[l] = counts.GetValueOrDefault(l) + 1;
        }
        var list = new List<FilterOption> { new(Any, "Any audio") };
        if (multi > 0) list.Add(new("multi", $"🔊 2+ audio tracks ({multi})"));
        list.AddRange(counts.OrderByDescending(kv => kv.Value).ThenBy(kv => LanguageName(kv.Key))
                            .Select(kv => new FilterOption($"lang:{kv.Key}", $"{LanguageName(kv.Key)} ({kv.Value})")));
        return list;
    }

    /// <summary>
    /// Subtitle filter choices: "none", then each language present (in the file or as a
    /// sidecar), then "No &lt;language&gt; subtitles" for the same languages.
    /// </summary>
    public static List<FilterOption> BuildSubtitleOptions(
        IEnumerable<(string? Encoded, IEnumerable<string> SidecarLangs, int SidecarCount)> entries)
    {
        var have = new Dictionary<string, int>();
        int none = 0, known = 0;
        foreach (var (enc, sideLangs, sideCount) in entries)
        {
            var langs = sideLangs.Select(NormalizeLang).ToHashSet();
            if (enc != null)
            {
                known++;
                var inFile = Decode(enc).Where(t => !t.IsAudio).ToList();
                foreach (var t in inFile) langs.Add(t.Lang);
                if (inFile.Count == 0 && sideCount == 0) none++;
            }
            foreach (var l in langs) if (l != "und") have[l] = have.GetValueOrDefault(l) + 1;
        }

        var ordered = have.OrderByDescending(kv => kv.Value).ThenBy(kv => LanguageName(kv.Key)).ToList();
        var list = new List<FilterOption> { new(Any, "Any subtitles") };
        if (known > 0) list.Add(new("none", $"💬 No subtitles at all ({none})"));
        list.AddRange(ordered.Select(kv => new FilterOption($"lang:{kv.Key}", $"{LanguageName(kv.Key)} ({kv.Value})")));
        if (known > 0)
            list.AddRange(ordered.Select(kv => new FilterOption($"missing:{kv.Key}", $"No {LanguageName(kv.Key)} subtitles")));
        return list;
    }
}
