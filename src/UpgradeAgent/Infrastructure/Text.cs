namespace UpgradeAgent.Infrastructure;

internal static class Text
{
    public static string Truncate(this string text, int length)
    {
        var singleLine = text.ReplaceLineEndings(" ");
        return singleLine.Length <= length ? singleLine : singleLine[..(length - 1)] + "…";
    }

    public static IReadOnlyList<string> TailLines(this string text, int count) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(count).ToList();

    public static string ShortSha(this string sha) => sha.Length <= 8 ? sha : sha[..8];

    public static string JoinLimited(this IEnumerable<string> items, int max, string separator = "; ")
    {
        var list = items.ToList();
        var shown = string.Join(separator, list.Take(max));
        return list.Count > max ? $"{shown} (+{list.Count - max} more)" : shown;
    }
}
