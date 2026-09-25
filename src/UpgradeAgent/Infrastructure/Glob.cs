using System.Text.RegularExpressions;

namespace UpgradeAgent.Infrastructure;

/// <summary>Case-insensitive <c>*</c> / <c>?</c> wildcard matching for package IDs and file names.</summary>
internal static class Glob
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    public static bool IsMatch(string pattern, string value) => ToRegex(pattern).IsMatch(value);

    public static bool IsMatchAny(IEnumerable<string> patterns, string value) => patterns.Any(p => IsMatch(p, value));

    /// <summary>Compile once when the same pattern is matched many times.</summary>
    public static Regex ToRegex(string pattern) =>
        new("^" + Regex.Escape(pattern).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
}
