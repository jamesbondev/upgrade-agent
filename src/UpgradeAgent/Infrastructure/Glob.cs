using System.Text.RegularExpressions;

namespace UpgradeAgent.Infrastructure;

/// <summary>Case-insensitive <c>*</c> / <c>?</c> wildcard matching for package IDs.</summary>
internal static class Glob
{
    public static bool IsMatch(string pattern, string value) =>
        Regex.IsMatch(value, ToRegex(pattern), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static bool IsMatchAny(IEnumerable<string> patterns, string value) => patterns.Any(p => IsMatch(p, value));

    private static string ToRegex(string pattern) =>
        "^" + Regex.Escape(pattern).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + "$";
}
