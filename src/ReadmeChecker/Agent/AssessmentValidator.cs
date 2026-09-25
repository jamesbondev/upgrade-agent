using System.Text.RegularExpressions;
using ReadmeChecker.Detection;

namespace ReadmeChecker.Agent;

internal sealed record RejectedIssue(ReadmeIssue Issue, string Reason);

internal sealed record IssueValidation(IReadOnlyList<ReadmeIssue> Accepted, IReadOnlyList<RejectedIssue> Rejected);

internal static partial class AssessmentValidator
{
    public const int MaxIssues = 30;

    public static IssueValidation Validate(ReadmeAssessment assessment, RepoFacts facts, IReadOnlyList<Signal> signals)
    {
        var readme = NormalizeText(facts.Readme?.Text ?? "");
        var accepted = new List<ReadmeIssue>();
        var rejected = new List<RejectedIssue>();

        foreach (var issue in assessment.Issues.Take(MaxIssues))
        {
            if (Check(issue, readme, facts, signals) is { } reason)
            {
                rejected.Add(new RejectedIssue(issue, reason));
            }
            else
            {
                accepted.Add(issue with { Evidence = issue.Evidence.Select(e => NormalizePath(e)!).Distinct(StringComparer.Ordinal).ToList() });
            }
        }

        rejected.AddRange(assessment.Issues.Skip(MaxIssues).Select(i => new RejectedIssue(i, $"more than {MaxIssues} issues")));
        return new IssueValidation(accepted, rejected);
    }

    private static string? Check(ReadmeIssue issue, string readme, RepoFacts facts, IReadOnlyList<Signal> signals)
    {
        var quote = NormalizeText(issue.Quote);
        if (quote.Length < 3)
        {
            return "the quote is empty";
        }

        if (!readme.Contains(quote, StringComparison.Ordinal) && !readme.Contains(NormalizeText(issue.Quote.Trim('`', '"', '\'')), StringComparison.Ordinal))
        {
            return "the quote isn't in the README";
        }

        var missing = issue.Evidence.Where(e => NormalizePath(e) is not { } path || !facts.Exists(path)).ToList();
        if (missing.Count > 0)
        {
            return $"evidence not in the repository: {string.Join(", ", missing)}";
        }

        return (issue.Evidence.Count, issue.Kind) switch
        {
            (0, IssueKind.BrokenReference) when !signals.Any(s => BacksABrokenReference(s) && issue.Quote.Contains(s.Text, StringComparison.Ordinal))
                => "a broken reference needs evidence, or a broken link or missing path the script found",
            (0, not (IssueKind.BrokenReference or IssueKind.Other)) => "no evidence from the repository",
            _ => null,
        };
    }

    private static bool BacksABrokenReference(Signal signal) =>
        signal.Definitive
        || (signal.Kind is SignalKind.MissingPath or SignalKind.MissingCommandTarget && signal.Target?.Contains('/', StringComparison.Ordinal) == true);

    internal static string NormalizeText(string text) => Whitespace().Replace(text.ReplaceLineEndings("\n"), " ").Trim();

    internal static string? NormalizePath(string path)
    {
        var trimmed = path.Trim().Trim('`', '"', '\'').Replace('\\', '/');
        return trimmed.Length == 0 || trimmed.StartsWith('/') || trimmed.Contains(':', StringComparison.Ordinal)
            ? null
            : ReadmeSignals.Normalize(trimmed);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
