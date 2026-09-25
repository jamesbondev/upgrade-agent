using System.Globalization;
using System.Text;
using System.Text.Json;
using ReadmeChecker.Run;

namespace ReadmeChecker.Reporting;

internal static class FixReportWriter
{
    public static async Task WriteRepoAsync(RepoFixReport report, string outputDirectory, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(outputDirectory, ReportWriter.FolderName(report.Name));
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, "fix.json"), JsonSerializer.Serialize(report, ReportWriter.Json), cancellationToken);
    }

    public static async Task<string> WriteAsync(FixRunReport report, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(report.OutputDirectory);
        await File.WriteAllTextAsync(Path.Combine(report.OutputDirectory, "fix-report.json"), JsonSerializer.Serialize(report, ReportWriter.Json), cancellationToken);
        var markdownPath = Path.Combine(report.OutputDirectory, "fix-report.md");
        await File.WriteAllTextAsync(markdownPath, Markdown(report), cancellationToken);
        return markdownPath;
    }

    internal static string Markdown(FixRunReport report)
    {
        var builder = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"# README fix, run {report.RunId}")
            .AppendLine()
            .AppendLine(CultureInfo.InvariantCulture, $"{report.Repos.Count} repos in {report.Duration.TotalMinutes:0.0} min. AI credits: {report.AiCredits:0.##}.")
            .AppendLine()
            .AppendLine("| Repo | Result | Check | Details |")
            .AppendLine("|---|---|---|---|");

        foreach (var repo in report.Repos.OrderBy(r => r.Status).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
        {
            var details = repo.PullRequestUrl ?? repo.Reason ?? "";
            builder.AppendLine(CultureInfo.InvariantCulture, $"| {ReportWriter.Escape(repo.Name)} | {repo.Status} | {repo.Check.Verdict} | {ReportWriter.Escape(details)} |");
        }

        foreach (var repo in report.Repos.Where(r => r.Status is not (FixStatus.NothingToFix or FixStatus.Skipped)))
        {
            builder.AppendLine()
                .AppendLine(CultureInfo.InvariantCulture, $"## {ReportWriter.Escape(repo.Name)}: {repo.Status}")
                .AppendLine();
            if (repo.Reason is not null)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"{ReportWriter.Escape(repo.Reason)}").AppendLine();
            }

            foreach (var problem in repo.Problems)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"- {ReportWriter.Escape(problem)}");
            }

            if (repo.PatchPath is not null)
            {
                builder.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"Patch: {ReportWriter.Escape(repo.PatchPath)}");
            }

            if (repo.AgentSummary is not null)
            {
                builder.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"Agent: {ReportWriter.Escape(repo.AgentSummary)}");
            }
        }

        return builder.ToString();
    }
}
