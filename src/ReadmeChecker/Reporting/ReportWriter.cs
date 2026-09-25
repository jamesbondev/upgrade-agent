using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ReadmeChecker.Agent;
using ReadmeChecker.Run;

namespace ReadmeChecker.Reporting;

internal static class ReportWriter
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string FolderName(string repoName)
    {
        var invalid = Path.GetInvalidFileNameChars().Append('/').Append('\\').ToHashSet();
        var safe = new string(repoName.Select(c => invalid.Contains(c) || char.IsWhiteSpace(c) ? '-' : c).ToArray()).Trim('.', '-');
        return safe.Length == 0 ? "repo" : safe;
    }

    public static async Task WriteRepoAsync(RepoReport report, string outputDirectory, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(outputDirectory, FolderName(report.Name));
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "assessment.json"), JsonSerializer.Serialize(report, Json), cancellationToken);
    }

    public static async Task<string> WriteAsync(CheckReport report, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(report.OutputDirectory);
        await File.WriteAllTextAsync(Path.Combine(report.OutputDirectory, "report.json"), JsonSerializer.Serialize(report, Json), cancellationToken);
        var markdownPath = Path.Combine(report.OutputDirectory, "report.md");
        await File.WriteAllTextAsync(markdownPath, Markdown(report), cancellationToken);
        return markdownPath;
    }

    internal static IEnumerable<RepoReport> Ordered(IEnumerable<RepoReport> repos) =>
        repos.OrderBy(r => r.Verdict)
            .ThenByDescending(r => r.Issues.Count)
            .ThenByDescending(r => r.CommitsSinceReadme ?? 0)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase);

    internal static string Markdown(CheckReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine(CultureInfo.InvariantCulture, $"# README check, run {report.RunId}")
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"{report.Repos.Count} repos in {report.Duration.TotalMinutes:0.0} min: {Counts(report.Repos)}. AI credits: {report.AiCredits:0.##}.")
            .AppendLine()
            .AppendLine("Issues come from an agent and were checked only for a verbatim quote and existing evidence. Signals come from a script and can be false positives.")
            .AppendLine()
            .AppendLine("| Repo | Verdict | Issues | Signals | README last changed | Commits since |")
            .AppendLine("|---|---|---|---|---|---|");

        var ordered = Ordered(report.Repos).ToList();
        foreach (var repo in ordered)
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"| {Escape(repo.Name)} | {Verdict(repo)} | {repo.Issues.Count} | {repo.Signals.Count + repo.SignalsNotListed} | {repo.ReadmeLastChanged?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "-"} | {repo.CommitsSinceReadme?.ToString(CultureInfo.InvariantCulture) ?? "-"} |");
        }

        foreach (var repo in ordered)
        {
            AppendRepo(builder, repo);
        }

        return builder.ToString();
    }

    private static void AppendRepo(StringBuilder builder, RepoReport repo)
    {
        builder.AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"## {Escape(repo.Name)}: {Verdict(repo)}")
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"{Escape(repo.Location)}{(repo.ReadmePath is null ? "" : $" · {Escape(repo.ReadmePath)}")}");

        if (repo.Note is not null)
        {
            builder.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"Note: {Escape(repo.Note)}");
        }

        if (repo.AgentSummary is not null)
        {
            builder.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"Agent summary: {Escape(repo.AgentSummary)}");
        }

        if (repo.Issues.Count > 0)
        {
            builder.AppendLine().AppendLine("### Issues").AppendLine();
            foreach (var (issue, index) in repo.Issues.Select((i, n) => (i, n + 1)))
            {
                var where = issue.Line is { } line ? $"line {line}, " : "";
                builder.AppendLine(CultureInfo.InvariantCulture, $"{index}. {where}**{issue.Kind}**: \"{Escape(issue.Quote)}\". {Escape(issue.SuggestedFix)}{Evidence(issue)}");
                if (issue.Truth is { } truth)
                {
                    builder.AppendLine(CultureInfo.InvariantCulture, $"   The code says: {Escape(truth)}{(issue.EvidenceQuote is { } quote ? $" (`{quote.ReplaceLineEndings(" ").Replace("`", "'", StringComparison.Ordinal)}`)" : "")}");
                }

                if (issue.MissingTerm is { } term)
                {
                    builder.AppendLine(CultureInfo.InvariantCulture, $"   No code or config file mentions \"{Escape(term)}\".");
                }
            }
        }

        if (repo.Coverage is { } coverage)
        {
            builder.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"### Coverage: {coverage.ClaimsChecked} claims checked").AppendLine();
            foreach (var part in coverage.Parts)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"- lines {part.FirstLine}-{part.LastLine}: {part.Status}, {part.ClaimsChecked} claims, {part.AiCredits:0.##} AI credits{(part.Note is null ? "" : $" ({Escape(part.Note)})")}");
            }

            foreach (var range in coverage.NotChecked)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"- lines {range.FirstLine}-{range.LastLine}: not checked (too long)");
            }
        }

        if (repo.Signals.Count > 0)
        {
            builder.AppendLine().AppendLine("### Signals").AppendLine();
            foreach (var signal in repo.Signals)
            {
                var where = signal.Line > 0 ? $"line {signal.Line}" : "not in the README";
                builder.AppendLine(CultureInfo.InvariantCulture, $"- {where}, {AssessmentPrompts.Describe(signal.Kind)}: {Escape(signal.Text)} ({Escape(signal.Detail)})");
            }

            if (repo.SignalsNotListed > 0)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"- and {repo.SignalsNotListed} more");
            }
        }

        if (repo.Unsupported.Count > 0)
        {
            builder.AppendLine().AppendLine("### Dropped: not supported by the README or repository").AppendLine();
            foreach (var rejected in repo.Unsupported)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"- \"{Escape(rejected.Issue.Quote)}\": {Escape(rejected.Reason)}");
            }
        }

        if (repo.Stats is { } stats)
        {
            builder.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"Agent: {Escape(stats.Model ?? "unknown model")}, {stats}");
        }
    }

    private static string Evidence(ReadmeIssue issue) =>
        issue.Evidence.Count == 0 ? "" : $" Evidence: {string.Join(", ", issue.Evidence.Select(Escape))}.";

    private static string Verdict(RepoReport repo) => repo.Deterministic ? $"{repo.Verdict} (no agent)" : repo.Verdict.ToString();

    private static string Counts(IEnumerable<RepoReport> repos) =>
        string.Join(", ", repos.GroupBy(r => r.Verdict).OrderBy(g => g.Key).Select(g => $"{g.Count()} {g.Key.ToString().ToLowerInvariant()}"));

    internal static string Escape(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.ReplaceLineEndings(" "))
        {
            if ("\\`*_[]<>|#".Contains(c, StringComparison.Ordinal))
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
