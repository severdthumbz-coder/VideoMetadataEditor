namespace VideoMetadataEditor.Services;

/// <summary>
/// "Natural" filename order, like Windows Explorer: numbers compare by value, so
/// "Episode 2" sorts before "Episode 10" and "S01E09" before "S01E10". Text compares
/// case-insensitively. Used to keep the Files panel in a predictable order (build 170).
/// </summary>
public sealed class NaturalStringComparer : IComparer<string?>
{
    public static readonly NaturalStringComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsDigit(x[i]) && char.IsDigit(y[j]))
            {
                int si = i, sj = j;
                while (i < x.Length && char.IsDigit(x[i])) i++;
                while (j < y.Length && char.IsDigit(y[j])) j++;
                var a = x.AsSpan(si, i - si).TrimStart('0');
                var b = y.AsSpan(sj, j - sj).TrimStart('0');
                if (a.Length != b.Length) return a.Length.CompareTo(b.Length);   // more digits = bigger
                int c = a.SequenceCompareTo(b);
                if (c != 0) return c;
                // equal value: fewer leading zeros first ("1" before "01"), stable either way
                int z = (i - si).CompareTo(j - sj);
                if (z != 0) return z;
            }
            else
            {
                int c = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                if (c != 0) return c;
                i++; j++;
            }
        }
        return (x.Length - i).CompareTo(y.Length - j);
    }
}
