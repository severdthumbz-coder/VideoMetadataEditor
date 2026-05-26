using System.Globalization;
using System.IO;
using System.Windows;

namespace VideoMetadataEditor.Services;

/// <summary>
/// Manages application language via merged ResourceDictionary.
///
/// Loading strategy:
///   1. English strings are defined inline in code (FallbackEnglishStrings) —
///      the app ALWAYS has readable UI even if the Languages/ folder is absent.
///   2. If Languages/Strings.en.xaml exists on disk it is loaded and merged,
///      overriding the inline strings (allows translation updates without recompile).
///   3. Language-specific overrides (Strings.es.xaml etc.) are merged on top.
///
/// Why inline fallback:
///   WPF ResourceDictionary.Source only accepts pack:// URIs for resource files
///   compiled into the assembly. Loose XAML files on disk require absolute
///   file:// URIs. If the Languages/ folder is missing, pack:// resolution silently
///   fails and every DynamicResource binding returns null — blank buttons.
/// </summary>
public static class LanguageService
{
    public static readonly IReadOnlyList<(string Code, string NativeName, string EnglishName)> SupportedLanguages =
    [
        ("System",   "System default",     "System default"),
        ("en",       "English",            "English"),
        // Partial translations (57% complete) — fall back to English for missing strings
        ("de",       "Deutsch",            "German"),
        ("es",       "Español",            "Spanish"),
        ("fr",       "Français",           "French"),
        ("it",       "Italiano",           "Italian"),
        // Partial translations (27–44% complete)
        ("ja",       "日本語",              "Japanese"),
        ("ko",       "한국어",              "Korean"),
        // NOTE: ar, pt, ru, zh-Hans have 0 keys — not shown until translated.
        // NOTE: zh-Hant, tr, sv, da, fi, pl, cs have no language file — not shown.
    ];

    private static string _currentCode = "System";
    public  static string CurrentCode  => _currentCode;

    // ── Inline English fallback (always applied first) ────────────────────────
    private static readonly Dictionary<string, string> FallbackEnglishStrings = new()
    {
        // Tabs
        ["Tab.RawData"]        = "📝 RAW DATA",
        ["Tab.RetrievedData"]  = "🔍 RETRIEVED DATA",
        ["Tab.MoveCopy"]       = "📦 MOVE / COPY",
        ["Tab.Settings"]       = "⚙️ SETTINGS",
        ["Tab.Library"]        = "📚 LIBRARY",
        ["Tab.Duplicates"]     = "🔍 DUPLICATES",
        ["Tab.Help"]           = "❓ HELP",
        ["Tab.Log"]            = "📋 LOG",
        // Toolbar
        ["Btn.AddFiles"]       = "📂 Add Files",
        ["Btn.AddFolder"]      = "📁 Add Folder",
        ["Btn.Recursive"]      = "Recursive",
        ["Btn.All"]            = "☑ All",
        ["Btn.None"]           = "☐ None",
        ["Btn.Lock"]           = "🔒 Lock",
        ["Btn.Unlock"]         = "🔓 Unlock",
        ["Btn.BatchProcess"]   = "⚡ Batch Process",
        ["Btn.TvBatch"]        = "📺 TV Batch",
        ["Btn.BatchRename"]    = "Batch Rename",
        ["Btn.SelectUnwatched"]= "Select Unwatched",
        ["Btn.SelectWatched"]  = "✓ Select Watched",
        // Files panel
        ["Files.Header"]       = "FILES",
        ["Files.Refresh"]      = "↺ Refresh",
        ["Files.Clear"]        = "✕ Clear",
        ["Files.RemoveFromList"]= "↩ Remove from List",
        ["Files.DeleteFile"]   = "🗑 Delete File",
        ["Files.UndoLastEmbed"]= "↩ Undo Last Embed",
        // Library
        ["Lib.Browse"]         = "Browse",
        ["Lib.Recursive"]      = "✓ Recursive",
        ["Lib.Folder"]         = "+ Folder",
        ["Lib.ScanLibrary"]    = "🔄 Scan Library",
        ["Lib.ExportCsv"]      = "Export CSV",
        ["Lib.ExportXlsx"]     = "Export XLSX",
        ["Lib.LoadSelected"]   = "Load Selected",
        ["Lib.Columns"]        = "⚙ Columns",
        ["Lib.SaveLayout"]     = "💾 Save Layout",
        ["Lib.FilterPlaceholder"]= "Filter by title, genre, director, year, cast…",
        ["Lib.TreeFilterPlaceholder"] = "🔎 Filter shows…",
        ["Lib.TreeSort.AZ"]    = "A → Z",
        ["Lib.TreeSort.EpisodeCount"] = "Episode count",
        ["Lib.TreeSort.RecentlyAdded"] = "Recently added",
        // Settings
        ["Set.ApiKeys"]        = "🔑 API Keys",
        ["Set.AniList"]        = "🎌 AniList (Anime)",
        ["Set.RenamePattern"]  = "🎬 Smart File Rename Pattern",
        ["Set.SmartOrganise"]  = "🗂 Move / Copy — Smart Organisation",
        ["Set.SavedSettings"]  = "📦 Move / Copy — Saved Settings",
        ["Set.LibraryDisplay"] = "📚 Library Display",
        ["Set.MkvArtwork"]     = "🎞 MKV Artwork Engine",
        ["Set.CopyEngine"]     = "🔧 Copy Engine",
        ["Set.WatchFolder"]    = "📁 Watch Folder",
        ["Set.AddFolder"]      = "📁 Add Folder",
        ["Set.Language"]       = "🌐 Language",
        ["Set.TempFiles"]      = "🗑 Temp File Handling",
        ["Set.Duplicates"]     = "🔍 Duplicates Tab",
        // Language settings
        ["Lang.Label"]         = "APPLICATION LANGUAGE",
        ["Lang.RestartNote"]   = "Date/number formats change immediately. UI text updates live. The application name always remains Video Metadata Editor.",
        // Move/Copy
        ["Copy.CopyFiles"]     = "📋 Copy Files",
        ["Copy.MoveFiles"]     = "✂ Move Files",
        ["Copy.SmartOrganise"] = "Smart Organise (Movies / TV Shows subfolders)",
    };

    // ── Startup ───────────────────────────────────────────────────────────────
    public static void ApplyOnStartup(string languageCode)
    {
        _currentCode = languageCode ?? "System";

        // 1. Always inject the inline English fallback first
        InjectFallbackStrings();

        // 2. Optionally overlay with file-based dictionaries
        var effectiveCode = ResolveEffectiveCode(_currentCode);
        TryLoadFileDict("en");  // file overrides inline (allows update without recompile)
        if (!string.Equals(effectiveCode, "en", StringComparison.OrdinalIgnoreCase))
            TryLoadFileDict(effectiveCode);

        ApplyCulture(_currentCode);
    }

    // ── Runtime change ────────────────────────────────────────────────────────
    public static void Apply(string languageCode)
    {
        _currentCode = languageCode ?? "System";
        var effectiveCode = ResolveEffectiveCode(_currentCode);

        // Remove previous language override (keep inline fallback + English file dict)
        RemoveTaggedDicts("lang-override");

        // Add new language override
        if (!string.Equals(effectiveCode, "en", StringComparison.OrdinalIgnoreCase))
            TryLoadFileDict(effectiveCode, tag: "lang-override");

        ApplyCulture(_currentCode);
    }

    // ── Core helpers ──────────────────────────────────────────────────────────

    private static void InjectFallbackStrings()
    {
        var app = Application.Current;
        if (app == null) return;

        // Remove any previously injected fallback dict
        RemoveTaggedDicts("lang-fallback");

        var dict = new ResourceDictionary();
        foreach (var (k, v) in FallbackEnglishStrings)
            dict[k] = v;
        dict["__tag"] = "lang-fallback";

        // Insert at position 0 so language files can override it
        app.Resources.MergedDictionaries.Insert(0, dict);
    }

    private static void TryLoadFileDict(string code, string tag = "lang-file")
    {
        var app = Application.Current;
        if (app == null) return;

        var filePath = Path.Combine(AppContext.BaseDirectory, "Languages",
            $"Strings.{code}.xaml");

        if (!File.Exists(filePath)) return;

        try
        {
            // Use absolute file:// URI — required for loose XAML files not in the assembly
            var uri  = new Uri(filePath, UriKind.Absolute);
            var dict = new ResourceDictionary { Source = uri };
            dict["__tag"] = tag;
            app.Resources.MergedDictionaries.Add(dict);
        }
        catch { /* corrupt or incompatible file — silently fall back */ }
    }

    private static void RemoveTaggedDicts(string tag)
    {
        var app = Application.Current;
        if (app == null) return;

        var toRemove = app.Resources.MergedDictionaries
            .Where(d => d.Contains("__tag") && d["__tag"] as string == tag)
            .ToList();
        foreach (var d in toRemove)
            app.Resources.MergedDictionaries.Remove(d);
    }

    private static string ResolveEffectiveCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code) || code == "System")
        {
            var twoL = CultureInfo.InstalledUICulture.TwoLetterISOLanguageName.ToLower();
            return SupportedLanguages.Any(l => l.Code == twoL) ? twoL : "en";
        }
        return code;
    }

    private static void ApplyCulture(string code)
    {
        CultureInfo culture;
        if (code == "System" || string.IsNullOrWhiteSpace(code))
            culture = CultureInfo.InstalledUICulture;
        else
        {
            try   { culture = new CultureInfo(code); }
            catch { culture = CultureInfo.InvariantCulture; }
        }

        System.Threading.Thread.CurrentThread.CurrentCulture   = culture;
        System.Threading.Thread.CurrentThread.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture                = culture;
        CultureInfo.DefaultThreadCurrentUICulture              = culture;

        if (Application.Current?.MainWindow != null)
        {
            var rtl = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "ar", "he", "fa", "ur" };
            Application.Current.MainWindow.FlowDirection =
                rtl.Any(r => code.StartsWith(r, StringComparison.OrdinalIgnoreCase))
                    ? FlowDirection.RightToLeft
                    : FlowDirection.LeftToRight;
        }
    }

    public static string GetDisplayName(string code)
    {
        var lang = SupportedLanguages.FirstOrDefault(l =>
            string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));
        if (lang == default) return code;
        return code == "System"
            ? $"System default  ({CultureInfo.InstalledUICulture.DisplayName})"
            : $"{lang.NativeName}  ({lang.EnglishName})";
    }
}
