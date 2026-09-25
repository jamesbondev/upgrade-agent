using System.Text.RegularExpressions;

namespace UpgradeAgent.Infrastructure;

internal static class Glob
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    public static bool IsMatch(string pattern, string value) => ToRegex(pattern).IsMatch(value);

    public static bool IsMatchAny(IEnumerable<string> patterns, string value) => patterns.Any(p => IsMatch(p, value));

    public static Regex ToRegex(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
}
