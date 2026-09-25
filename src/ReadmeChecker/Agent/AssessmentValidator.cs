using System.Text;
using System.Text.RegularExpressions;
using ReadmeChecker.Detection;
using RepoKit;

namespace ReadmeChecker.Agent;

internal sealed record RejectedIssue(ReadmeIssue Issue, string Reason);

internal sealed record IssueValidation(IReadOnlyList<ReadmeIssue> Accepted, IReadOnlyList<RejectedIssue> Rejected);

internal sealed record ValidationScope(string Root, GitCli Git, RepoFacts Facts, IReadOnlyList<Signal> Signals, bool Deep = false, int FirstLine = 1, int LastLine = int.MaxValue);

internal static partial class AssessmentValidator
{
    public const int MaxIssues = 30;

    public static async Task<IssueValidation> ValidateAsync(IReadOnlyList<ReadmeIssue> issues, ValidationScope scope, CancellationToken cancellationToken)
    {
        var readme = scope.Facts.Readme?.Text ?? "";
        var located = LocatedText.Of(readme);
        var accepted = new List<ReadmeIssue>();
        var rejected = new List<RejectedIssue>();

        foreach (var issue in issues.Take(MaxIssues))
        {
            var line = QuoteLine(issue.Quote, located, scope);
            var reason = line is null ? QuoteProblem(issue.Quote, scope) : await CheckAsync(issue, readme, scope, cancellationToken);
            if (reason is not null)
            {
                rejected.Add(new RejectedIssue(issue, reason));
                continue;
            }

            var normalized = issue with
            {
                Evidence = issue.Evidence.Select(NormalizePath).OfType<string>().Distinct(StringComparer.Ordinal).ToList(),
                Line = line,
            };
            if (accepted.Any(a => a.Kind == normalized.Kind && NormalizeText(a.Quote) == NormalizeText(normalized.Quote)))
            {
                continue;
            }

            accepted.Add(normalized);
        }

        rejected.AddRange(issues.Skip(MaxIssues).Select(i => new RejectedIssue(i, $"more than {MaxIssues} issues")));
        return new IssueValidation(accepted, rejected);
    }

    private static async Task<string?> CheckAsync(ReadmeIssue issue, string readme, ValidationScope scope, CancellationToken cancellationToken)
    {
        var missing = issue.Evidence.Where(e => NormalizePath(e) is not { } path || !scope.Facts.Exists(path)).ToList();
        if (missing.Count > 0)
        {
            return $"evidence not in the repository: {string.Join(", ", missing)}";
        }

        var hasQuote = !string.IsNullOrWhiteSpace(issue.EvidenceQuote);
        var hasTerm = !string.IsNullOrWhiteSpace(issue.MissingTerm);
        if (hasQuote && EvidenceChecks.QuoteProblem(issue, scope.Root, scope.Facts) is { } quoteProblem)
        {
            return quoteProblem;
        }

        if (hasTerm && await EvidenceChecks.MissingTermProblemAsync(issue, readme, scope.Root, scope.Git, cancellationToken) is { } termProblem)
        {
            return termProblem;
        }

        var signalBacked = issue.Kind == IssueKind.BrokenReference
            && scope.Signals.Any(s => BacksABrokenReference(s) && issue.Quote.Contains(s.Text, StringComparison.Ordinal));

        if (issue.Kind == IssueKind.WrongClaim)
        {
            return string.IsNullOrWhiteSpace(issue.Truth) ? "a wrong claim needs the truth from the code"
                : !hasQuote && !hasTerm ? "a wrong claim needs an evidence quote or a missing term"
                : null;
        }

        return (issue.Evidence.Count, issue.Kind) switch
        {
            (0, IssueKind.BrokenReference) when !signalBacked && !hasTerm
                => "a broken reference needs evidence, or a broken link or missing path the script found",
            (0, not (IssueKind.BrokenReference or IssueKind.Other)) => "no evidence from the repository",
            (_, IssueKind.Other) when scope.Deep && !hasQuote && !hasTerm => "in a deep check every problem needs an evidence quote or a missing term",
            _ => null,
        };
    }

    private static int? QuoteLine(string quote, LocatedText located, ValidationScope scope)
    {
        var inScope = scope.Deep && !IsSignalBackedContent(quote, scope) ? scope : scope with { FirstLine = 1, LastLine = int.MaxValue };
        return located.LineOf(NormalizeText(quote), inScope.FirstLine, inScope.LastLine)
            ?? located.LineOf(NormalizeText(quote.Trim('`', '"', '\'')), inScope.FirstLine, inScope.LastLine);
    }

    private static string QuoteProblem(string quote, ValidationScope scope) =>
        NormalizeText(quote).Length < 3 ? "the quote is empty"
        : scope.Deep && LocatedText.Of(scope.Facts.Readme?.Text ?? "").LineOf(NormalizeText(quote), 1, int.MaxValue) is not null
            ? "the quote is in another part of the README than the one being checked"
            : "the quote isn't in the README";

    private static bool IsSignalBackedContent(string quote, ValidationScope scope) =>
        scope.Signals.Any(s => s is { Kind: SignalKind.UnmentionedProject or SignalKind.UnlistedFile } && quote.Contains(Path.GetFileNameWithoutExtension(s.Text), StringComparison.OrdinalIgnoreCase));

    private static bool BacksABrokenReference(Signal signal) =>
        signal.Definitive
        || signal.Kind == SignalKind.MissingIdentifier
        || (signal.Kind is SignalKind.MissingPath or SignalKind.MissingCommandTarget && signal.Target?.Contains('/', StringComparison.Ordinal) == true);

    internal static string NormalizeText(string text) => Whitespace().Replace(text.ReplaceLineEndings("\n"), " ").Trim();

    internal static string? NormalizePath(string path)
    {
        var trimmed = LineSuffix().Replace(path.Trim().Trim('`', '"', '\''), "").Replace('\\', '/');
        return trimmed.Length == 0 || trimmed.StartsWith('/') || trimmed.Contains(':', StringComparison.Ordinal)
            ? null
            : ReadmeSignals.Normalize(trimmed);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@":\d+(-\d+)?$")]
    private static partial Regex LineSuffix();

    private sealed class LocatedText(string text, int[] lines)
    {
        public static LocatedText Of(string readme)
        {
            var builder = new StringBuilder(readme.Length);
            var lines = new List<int>(readme.Length);
            var line = 1;
            var pendingSpace = false;
            foreach (var c in readme.ReplaceLineEndings("\n"))
            {
                if (char.IsWhiteSpace(c))
                {
                    pendingSpace = builder.Length > 0;
                }
                else
                {
                    if (pendingSpace)
                    {
                        builder.Append(' ');
                        lines.Add(line);
                        pendingSpace = false;
                    }

                    builder.Append(c);
                    lines.Add(line);
                }

                if (c == '\n')
                {
                    line++;
                }
            }

            return new LocatedText(builder.ToString(), [.. lines]);
        }

        public int? LineOf(string quote, int firstLine, int lastLine)
        {
            if (quote.Length == 0)
            {
                return null;
            }

            for (var index = text.IndexOf(quote, StringComparison.Ordinal); index >= 0; index = text.IndexOf(quote, index + 1, StringComparison.Ordinal))
            {
                if (lines[index] >= firstLine && lines[index] <= lastLine)
                {
                    return lines[index];
                }
            }

            return null;
        }
    }
}
