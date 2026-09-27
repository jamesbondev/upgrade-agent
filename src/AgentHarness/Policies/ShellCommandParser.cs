using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentHarness.Policies;

internal sealed record ParsedCommand(IReadOnlyList<IReadOnlyList<string>> Segments);

internal static partial class ShellCommandParser
{
    private const string BackslashEscapesRefusal = "backslash escapes are not allowed; use single quotes for literal text";

    private const string ExpansionRefusal = "variable expansion and command substitution are not allowed; write paths out in full";

    public static bool TryParse(string commandLine, [NotNullWhen(true)] out ParsedCommand? parsed, [NotNullWhen(false)] out string? error)
    {
        parsed = null;
        var segments = new List<IReadOnlyList<string>>();
        error = Tokenize(commandLine, segments);
        if (error is not null)
        {
            return false;
        }

        parsed = new ParsedCommand(segments);
        return true;
    }

    private static string? Tokenize(string commandLine, List<IReadOnlyList<string>> segments)
    {
        commandLine = StderrToStdout().Replace(commandLine, " ");
        List<string> tokens = [];
        var token = new StringBuilder();
        var hasToken = false;
        char? quote = null;

        void EndToken()
        {
            if (hasToken)
            {
                tokens.Add(token.ToString());
                token.Clear();
                hasToken = false;
            }
        }

        void EndSegment()
        {
            EndToken();
            if (tokens.Count > 0)
            {
                segments.Add(tokens);
                tokens = [];
            }
        }

        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            var next = i + 1 < commandLine.Length ? commandLine[i + 1] : '\0';

            if (quote == '\'')
            {
                if (c == '\'')
                {
                    quote = null;
                }
                else
                {
                    token.Append(c);
                }

                continue;
            }

            if (quote == '"')
            {
                if (c == '"')
                {
                    quote = null;
                    continue;
                }

                if (c == '`')
                {
                    return "command substitution is not allowed";
                }

                if (c == '\\' && next is '"' or '$' or '`' or '\\')
                {
                    return BackslashEscapesRefusal;
                }

                if (c == '$' && IsExpansionStart(next))
                {
                    return ExpansionRefusal;
                }

                token.Append(c);
                continue;
            }

            switch (c)
            {
                case '\'' or '"':
                    quote = c;
                    hasToken = true;
                    break;
                case ' ' or '\t':
                    EndToken();
                    break;
                case '\r' or '\n':
                    return "multi-line commands are not allowed";
                case '`':
                    return "backticks are not allowed";
                case '\\' when IsEscapable(next):
                    return BackslashEscapesRefusal;
                case '$' when IsExpansionStart(next):
                    return ExpansionRefusal;
                case '(' or ')':
                    return "subshells and PowerShell subexpressions are not allowed";
                case '>' or '<':
                    return "redirection is not allowed; read output directly";
                case '{' or '}':
                    return "script blocks are not allowed";
                case ';':
                    EndSegment();
                    break;
                case '&' when next == '&':
                    EndSegment();
                    i++;
                    break;
                case '&':
                    return "background jobs are not allowed";
                case '|' when next == '|':
                    EndSegment();
                    i++;
                    break;
                case '|':
                    EndSegment();
                    break;
                default:
                    token.Append(c);
                    hasToken = true;
                    break;
            }
        }

        if (quote is not null)
        {
            return "unbalanced quotes";
        }

        EndSegment();
        return null;
    }

    private static bool IsEscapable(char next) =>
        next is '\'' or '"' or ';' or '&' or '|' or '`' or '$' or '(' or ')' or '<' or '>' or '{' or '}' or ' ' or '\t' or '\\' or '\r' or '\n' or '\0';

    private static bool IsExpansionStart(char next) => char.IsLetter(next) || next is '_' or '{' or '(' or '?' or '$' or '@' or '*' or '!' or '#';

    [GeneratedRegex(@"(?<=^|\s)2>&1(?=\s|$)")]
    private static partial Regex StderrToStdout();
}
