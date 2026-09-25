namespace UpgradeAgent.Infrastructure;

internal static class Text
{
    /// <summary>One line, at most <paramref name="length"/> characters, ending in "…" when cut.</summary>
    public static string Truncate(this string text, int length)
    {
        var singleLine = text.ReplaceLineEndings(" ");
        return singleLine.Length <= length ? singleLine : singleLine[..(length - 1)] + "…";
    }

    /// <summary>The last <paramref name="count"/> non-empty lines, trimmed.</summary>
    public static IReadOnlyList<string> TailLines(this string text, int count) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(count).ToList();

    /// <summary>The short commit id used everywhere a SHA is shown.</summary>
    public static string ShortSha(this string sha) => sha.Length <= 8 ? sha : sha[..8];

    /// <summary>The first <paramref name="max"/> items joined, plus "(+N more)" when some were left out.</summary>
    public static string JoinLimited(this IEnumerable<string> items, int max, string separator = "; ")
    {
        var list = items.ToList();
        var shown = string.Join(separator, list.Take(max));
        return list.Count > max ? $"{shown} (+{list.Count - max} more)" : shown;
    }
}
