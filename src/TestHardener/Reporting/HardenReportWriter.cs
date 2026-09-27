using System.Globalization;
using System.Text;
using System.Text.Json;
using TestHardener.Hardening;
using TestHardener.Run;

namespace TestHardener.Reporting;

internal static class HardenReportWriter
{
    private const int FeedbackLines = 15;

    public static async Task WriteGroupAsync(GroupResult result, string folder, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(result, SurveyReportWriter.Json), cancellationToken);
    }

    public static async Task WriteRepoAsync(RepoHardenReport report, string folder, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "harden.json"), JsonSerializer.Serialize(report, SurveyReportWriter.Json), cancellationToken);
    }

    public static async Task<string> WriteAsync(HardenReport report, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(report.OutputDirectory);
        await File.WriteAllTextAsync(Path.Combine(report.OutputDirectory, "harden-report.json"), JsonSerializer.Serialize(report, SurveyReportWriter.Json), cancellationToken);
        var path = Path.Combine(report.OutputDirectory, "harden-report.md");
        await File.WriteAllTextAsync(path, Markdown(report), cancellationToken);
        return path;
    }

    internal static string Markdown(HardenReport report)
    {
        var builder = new StringBuilder();
        builder.AppendLine(CultureInfo.InvariantCulture, $"# Test hardening, run {report.RunId}{(report.DryRun ? " (dry run)" : "")}")
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"{report.Repos.Count} repos in {report.Duration.TotalMinutes:0.0} min. AI credits: {report.AiCredits:0.##}.")
            .AppendLine()
            .AppendLine("Tests are written by an agent. TestHardener checked in code that only the one test file changed, by additions only; that the new tests build, pass on the current code every time, and each catch a surviving mutant, as Stryker confirmed. What each test asserts is the agent's own description and is not verified.")
            .AppendLine()
            .AppendLine("| Repo | Status | Groups verified | Mutants killed | Tests added | AI credits | Time |")
            .AppendLine("|---|---|---|---|---|---|---|");
        foreach (var repo in report.Repos)
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"| {Escape(repo.Name)} | {repo.Status} | {repo.Verified} of {repo.Groups.Count} | {repo.Killed} of {repo.Targeted} | {repo.TestsAdded} | {repo.AiCredits:0.##} | {repo.Duration.TotalMinutes:0.0} min |");
        }

        foreach (var repo in report.Repos)
        {
            AppendRepo(builder, repo);
        }

        return builder.ToString();
    }

    private static void AppendRepo(StringBuilder builder, RepoHardenReport repo)
    {
        builder.AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"## {Escape(repo.Name)}: {repo.Status}")
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"{Escape(repo.Location)}{(repo.Sha is null ? "" : $" at commit `{repo.Sha}`")}");
        if (repo.Note is not null)
        {
            builder.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"Note: {Escape(repo.Note)}");
        }

        if (repo.PullRequestUrl is not null)
        {
            builder.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"Draft pull request: {repo.PullRequestUrl} (branch `{repo.Branch}`)");
        }

        if (repo.LeftoverBranches.Count > 0)
        {
            builder.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"Pushed branches with no pull request, probably from a run that stopped between push and PR: {string.Join(", ", repo.LeftoverBranches.Select(b => $"`{b}`"))}. Open a PR for them or delete them.");
        }

        if (repo.PatchPath is not null)
        {
            builder.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"Patch: `{repo.PatchPath}` ({string.Join(", ", repo.ChangedFiles.Select(f => $"`{f}`"))})");
        }

        foreach (var group in repo.Groups)
        {
            AppendGroup(builder, group);
        }
    }

    private static void AppendGroup(StringBuilder builder, GroupResult group)
    {
        builder.AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"### {group.Number}. {Escape(group.Group.Member.Name)}: {group.Outcome}")
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"{Escape(group.Group.File)}, lines {group.Group.Member.StartLine}-{group.Group.Member.EndLine}, target {Escape(group.Target)}. {group.Rounds.Count} rounds, {group.Duration.TotalMinutes:0.0} min{Stats(group)}.");
        if (group.Owned is { } owned)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"Tests in `{owned.Path}`{(owned.IsNew ? " (new file)" : "")}, chosen because {Escape(owned.Reason)}.");
        }

        if (group.Final is { } final)
        {
            builder.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"Checks: {string.Join("; ", final.Steps.Select(s => $"{s.Name} {(s.Passed ? "✓" : "✗")}{(s.Passed && s.Detail is not null ? $" ({s.Detail})" : "")}"))}.");
            if (final.KillsPerTest.Count > 0)
            {
                builder.AppendLine().AppendLine("| Test | Mutants it kills |").AppendLine("|---|---|");
                foreach (var (test, kills) in final.KillsPerTest.OrderByDescending(k => k.Value))
                {
                    builder.AppendLine(CultureInfo.InvariantCulture, $"| {Escape(test)} | {kills} |");
                }
            }

            if (final.Survivors.Count > 0)
            {
                builder.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"Mutants: {string.Join(", ", final.Survivors.GroupBy(s => s.Status).Select(g => $"{g.Count()} {g.Key}"))}.");
            }
        }

        if (group.Summary is { } summary)
        {
            builder.AppendLine().AppendLine("The agent's account (unverified):").AppendLine();
            foreach (var test in summary.Tests)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"- {Escape(test.Name)}: {Escape(test.Asserts)}");
            }

            if (summary.BlockedBy != BlockedBy.None)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"- Blocked: {summary.BlockedBy}. {Escape(summary.BlockedReason ?? "")}");
            }
        }

        if (group.Outcome != GroupOutcome.Verified && group.Reason is { } reason)
        {
            builder.AppendLine().AppendLine("Why it wasn't kept:").AppendLine().AppendLine("```text");
            foreach (var line in reason.ReplaceLineEndings("\n").Split('\n').Take(FeedbackLines))
            {
                builder.AppendLine(line.Replace("```", "'''", StringComparison.Ordinal));
            }

            builder.AppendLine("```");
        }
    }

    private static string Stats(GroupResult group) =>
        group.Stats is { } stats ? string.Create(CultureInfo.InvariantCulture, $", {stats.AiCredits:0.##} AI credits, {stats.ToolCalls} tool calls, model {stats.Model ?? "unknown"}") : "";

    private static string Escape(string text) => SurveyReportWriter.Escape(text);
}
