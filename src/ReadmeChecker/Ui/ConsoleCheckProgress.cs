using System.Globalization;
using ReadmeChecker.Config;
using ReadmeChecker.Reporting;
using ReadmeChecker.Run;
using Spectre.Console;

namespace ReadmeChecker.Ui;

internal static class ConsoleFactory
{
    public static IAnsiConsole Create()
    {
        var console = AnsiConsole.Console;
        if (Console.IsOutputRedirected && int.TryParse(Environment.GetEnvironmentVariable("COLUMNS"), out var width) && width >= 40)
        {
            console.Profile.Width = width;
        }

        return console;
    }
}

internal sealed class ConsoleCheckProgress(IAnsiConsole console) : ICheckProgress
{
    public void RepoStarted(RepoTarget target, int index, int count) =>
        console.MarkupLine($"[grey]({index}/{count})[/] {Markup.Escape(target.Name)} [grey]{Markup.Escape(target.Location)}[/]");

    public void RepoFinished(RepoReport report)
    {
        var detail = report.Note ?? (report.Issues.Count > 0 ? $"{report.Issues.Count} issue(s)" : "");
        console.MarkupLine($"  {Colour(report.Verdict)} [grey]{Markup.Escape(detail)} · {report.Duration.TotalSeconds:0}s[/]");
    }

    public void RunFinished(CheckReport report, string reportPath)
    {
        var table = new Table().Border(TableBorder.Rounded)
            .AddColumns("Repo", "Verdict", "Issues", "Signals", "README changed", "Commits since");
        foreach (var repo in ReportWriter.Ordered(report.Repos))
        {
            table.AddRow(
                Markup.Escape(repo.Name),
                Colour(repo.Verdict) + (repo.Deterministic ? " [grey](no agent)[/]" : ""),
                repo.Issues.Count.ToString(CultureInfo.InvariantCulture),
                (repo.Signals.Count + repo.SignalsNotListed).ToString(CultureInfo.InvariantCulture),
                repo.ReadmeLastChanged?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "-",
                repo.CommitsSinceReadme?.ToString(CultureInfo.InvariantCulture) ?? "-");
        }

        console.WriteLine();
        console.Write(table);
        console.MarkupLine($"AI credits: {report.AiCredits:0.##} · {report.Duration.TotalMinutes:0.0} min");
        console.MarkupLine($"Report: [link]{Markup.Escape(reportPath)}[/]");
    }

    private static string Colour(RepoVerdict verdict) => verdict switch
    {
        RepoVerdict.Stale => "[yellow]Stale[/]",
        RepoVerdict.Current => "[green]Current[/]",
        RepoVerdict.Error => "[red]Error[/]",
        RepoVerdict.Missing => "[yellow]Missing[/]",
        _ => $"[blue]{verdict}[/]",
    };
}
