using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;

namespace UpgradeAgent.Agent;

/// <summary>A command line split into simple commands (by <c>&amp;&amp;</c>, <c>||</c>, <c>;</c> and <c>|</c>).</summary>
internal sealed record ParsedCommand(IReadOnlyList<IReadOnlyList<string>> Segments);

/// <summary>
/// A deliberately small POSIX/PowerShell-ish tokenizer. Anything it can't reason about is refused rather
/// than guessed at: substitution and variable expansion (<c>$(…)</c>, <c>$VAR</c>, backticks, PowerShell
/// <c>(…)</c> and <c>@(…)</c>), redirection, background jobs, script blocks and multi-line input. Single
/// quotes keep everything literal.
/// </summary>
internal static partial class ShellCommandParser
{
    public static bool TryParse(string commandLine, [NotNullWhen(true)] out ParsedCommand? parsed, [NotNullWhen(false)] out string? error)
    {
        parsed = null;
        error = Tokenize(commandLine, out var segments);
        if (error is not null)
        {
            return false;
        }

        parsed = new ParsedCommand(segments);
        return true;
    }

    private static string? Tokenize(string commandLine, out List<IReadOnlyList<string>> segments)
    {
        // "2>&1" only merges stderr into stdout; it writes no file. Agents append it habitually.
        commandLine = StderrToStdout().Replace(commandLine, " ");
        segments = [];
        var tokens = new List<string>();
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

        void EndSegment(List<IReadOnlyList<string>> into)
        {
            EndToken();
            if (tokens.Count > 0)
            {
                into.Add(tokens.ToList());
                tokens.Clear();
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

                if (c == '$' && IsExpansionStart(next))
                {
                    return "variable expansion and command substitution are not allowed; write paths out in full";
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
                case '$' when IsExpansionStart(next):
                    return "variable expansion and command substitution are not allowed; write paths out in full";
                case '(' or ')':
                    return "subshells and PowerShell subexpressions are not allowed";
                case '>' or '<':
                    return "redirection is not allowed; read output directly";
                case '{' or '}':
                    return "script blocks are not allowed";
                case ';':
                    EndSegment(segments);
                    break;
                case '&' when next == '&':
                    EndSegment(segments);
                    i++;
                    break;
                case '&':
                    return "background jobs are not allowed";
                case '|' when next == '|':
                    EndSegment(segments);
                    i++;
                    break;
                case '|':
                    EndSegment(segments);
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

        EndSegment(segments);
        return null;
    }

    /// <summary>What can follow <c>$</c> to expand: a name, <c>{</c>, <c>(</c>, or a special parameter.</summary>
    private static bool IsExpansionStart(char next) => char.IsLetter(next) || next is '_' or '{' or '(' or '?' or '$' or '@' or '*' or '!' or '#';

    [GeneratedRegex(@"(?<=^|\s)2>&1(?=\s|$)")]
    private static partial Regex StderrToStdout();
}
