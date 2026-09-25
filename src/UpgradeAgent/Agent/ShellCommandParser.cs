using System.Text;
using System.Text.RegularExpressions;

namespace UpgradeAgent.Agent;

/// <summary>A command line split into simple commands (by <c>&amp;&amp;</c>, <c>||</c>, <c>;</c> and <c>|</c>).</summary>
internal sealed record ParsedCommand(IReadOnlyList<IReadOnlyList<string>> Segments);

/// <summary>
/// A deliberately small POSIX/PowerShell-ish tokenizer. Anything it can't reason about (substitution,
/// redirection, background jobs, script blocks, multi-line input) is refused rather than guessed at.
/// </summary>
internal static partial class ShellCommandParser
{
    public static ParsedCommand Parse(string commandLine, out string? error)
    {
        error = null;

        // "2>&1" only merges stderr into stdout; it writes no file. Agents append it habitually.
        commandLine = StderrToStdout().Replace(commandLine, " ");
        var segments = new List<IReadOnlyList<string>>();
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

        void EndSegment()
        {
            EndToken();
            if (tokens.Count > 0)
            {
                segments.Add(tokens.ToList());
                tokens.Clear();
            }
        }

        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            var next = i + 1 < commandLine.Length ? commandLine[i + 1] : '\0';

            if (quote is not null)
            {
                if (c == quote)
                {
                    quote = null;
                }
                else if (quote == '"' && (c == '`' || (c == '$' && next == '(')))
                {
                    error = "command substitution is not allowed";
                    return new ParsedCommand([]);
                }
                else
                {
                    token.Append(c);
                }

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
                    error = "multi-line commands are not allowed";
                    return new ParsedCommand([]);
                case '`':
                    error = "backticks are not allowed";
                    return new ParsedCommand([]);
                case '$' when next == '(':
                    error = "command substitution is not allowed";
                    return new ParsedCommand([]);
                case '>' or '<':
                    error = "redirection is not allowed; read output directly";
                    return new ParsedCommand([]);
                case '{' or '}':
                    error = "script blocks are not allowed";
                    return new ParsedCommand([]);
                case ';':
                    EndSegment();
                    break;
                case '&' when next == '&':
                    EndSegment();
                    i++;
                    break;
                case '&':
                    error = "background jobs are not allowed";
                    return new ParsedCommand([]);
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
            error = "unbalanced quotes";
            return new ParsedCommand([]);
        }

        EndSegment();
        return new ParsedCommand(segments);
    }

    [GeneratedRegex(@"(?<=^|\s)2>&1(?=\s|$)")]
    private static partial Regex StderrToStdout();
}
