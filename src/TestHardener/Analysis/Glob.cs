using System.Text;
using System.Text.RegularExpressions;

namespace TestHardener.Analysis;

internal static class Glob
{
    public static bool IsMatch(string pattern, string path) =>
        Regex.IsMatch(path.Replace('\\', '/'), ToRegex(pattern.Replace('\\', '/')), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static bool AnyMatch(IEnumerable<string> patterns, string path) => patterns.Any(p => IsMatch(p, path));

    internal static string ToRegex(string pattern)
    {
        var builder = new StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                var slashAfter = i + 2 < pattern.Length && pattern[i + 2] == '/';
                builder.Append(slashAfter ? "(?:.*/)?" : ".*");
                i += slashAfter ? 2 : 1;
            }
            else if (c == '*')
            {
                builder.Append("[^/]*");
            }
            else if (c == '?')
            {
                builder.Append("[^/]");
            }
            else
            {
                builder.Append(Regex.Escape(c.ToString()));
            }
        }

        return builder.Append('$').ToString();
    }
}
