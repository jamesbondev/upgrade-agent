using System.Text;
using System.Text.RegularExpressions;

namespace ReadmeChecker.Detection;

internal static partial class ShellLine
{
    public static IEnumerable<IReadOnlyList<string>> Commands(string line)
    {
        var text = Prompt().Replace(line, "").Trim();
        var comment = text.IndexOf(" #", StringComparison.Ordinal);
        if (comment >= 0)
        {
            text = text[..comment].TrimEnd();
        }

        return IsComment(text) ? [] : Segments(Tokenize(text));
    }

    private static bool IsComment(string text) =>
        text.Length == 0 || text.StartsWith('#') || text.StartsWith("//", StringComparison.Ordinal) || text.StartsWith("REM ", StringComparison.OrdinalIgnoreCase);

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        foreach (var c in text)
        {
            if (quote is not null)
            {
                if (c == quote)
                {
                    quote = null;
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (char.IsWhiteSpace(c))
            {
                Flush();
            }
            else
            {
                current.Append(c);
            }
        }

        Flush();
        return tokens;

        void Flush()
        {
            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }
    }

    private static IEnumerable<IReadOnlyList<string>> Segments(List<string> tokens)
    {
        var segment = new List<string>();
        foreach (var token in tokens)
        {
            if (token is "&&" or "||" or ";" or "|")
            {
                if (segment.Count > 0)
                {
                    yield return segment;
                }

                segment = [];
            }
            else
            {
                segment.Add(token);
            }
        }

        if (segment.Count > 0)
        {
            yield return segment;
        }
    }

    [GeneratedRegex(@"^\s*(?:\$|>|PS[^>]*>)\s+")]
    private static partial Regex Prompt();
}
