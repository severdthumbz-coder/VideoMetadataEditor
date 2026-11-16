namespace VideoMetadataEditor.Services;

/// <summary>
/// Path helpers for the Library's extra-folder list (build 168). Kept WPF-free so
/// they're unit-tested.
/// </summary>
public static class LibraryFolderList
{
    /// <summary>Case-insensitive, slash- and trailing-separator-insensitive key for a folder.</summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        var p = path.Trim().Replace('/', '\\');
        if (p.Length > 3) p = p.TrimEnd('\\');   // keep a drive root like "A:\" intact
        return p.ToUpperInvariant();
    }

    /// <summary>
    /// Removes blank entries, duplicates (same folder differing only by case or a trailing
    /// slash) and the primary library folder. Keeps the first spelling of each.
    /// </summary>
    public static List<string> Dedupe(IEnumerable<string> folders, string? primary)
    {
        var seen = new HashSet<string> { Normalize(primary) };
        var result = new List<string>();
        foreach (var f in folders)
        {
            var key = Normalize(f);
            if (key.Length == 0 || !seen.Add(key)) continue;
            result.Add(f);
        }
        return result;
    }
}
