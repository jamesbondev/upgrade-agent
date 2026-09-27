using System.Globalization;
using Spectre.Console;
using TestHardener.Config;
using TestHardener.Run;

namespace TestHardener.Ui;

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

internal sealed class ConsoleSurveyProgress(IAnsiConsole console) : ISurveyProgress
{
    public void RepoStarted(RepoTarget target, int index, int count) =>
        console.MarkupLine($"[grey]({index}/{count})[/] {Markup.Escape(target.Name)} [grey]{Markup.Escape(target.Location)}[/]");

    public void TargetStarted(TargetConfig target, bool fromEarlierRun) =>
        console.MarkupLine($"  {Markup.Escape(target.Name)} [grey]{(fromEarlierRun ? "reusing the earlier Stryker report" : "running Stryker, which can take a while")}[/]");

    public void TargetFinished(TargetSurvey target)
    {
        var detail = target.Failure is { } failure
            ? $"[red]failed[/] [grey]{Markup.Escape(failure)}[/]"
            : $"[green]{Score(target.Score)}[/] [grey]{target.Survivors} survived, {target.Candidates} candidates in {target.Groups.Count} groups · {target.Duration.TotalMinutes:0.0} min[/]";
        console.MarkupLine($"    {detail}");
    }

    public void RepoFinished(RepoSurvey repo)
    {
        if (repo.Targets.Count == 0 || repo.Status == SurveyStatus.Failed)
        {
            console.MarkupLine($"  [red]{repo.Status}[/] [grey]{Markup.Escape(repo.Note ?? "")}[/]");
        }
    }

    public void RunFinished(SurveyReport report, string reportPath)
    {
        var table = new Table().Border(TableBorder.Rounded).AddColumns("Repo", "Target", "Score", "Survived", "Candidates", "Groups", "Time");
        foreach (var repo in report.Repos)
        {
            foreach (var target in repo.Targets)
            {
                table.AddRow(
                    Markup.Escape(repo.Name),
                    Markup.Escape(target.Name),
                    target.Failure is null ? Score(target.Score) : "[red]failed[/]",
                    target.Survivors.ToString(CultureInfo.InvariantCulture),
                    target.Candidates.ToString(CultureInfo.InvariantCulture),
                    target.Groups.Count.ToString(CultureInfo.InvariantCulture),
                    string.Create(CultureInfo.InvariantCulture, $"{target.Duration.TotalMinutes:0.0} min"));
            }
        }

        console.WriteLine();
        console.Write(table);
        console.MarkupLine($"{report.Duration.TotalMinutes:0.0} min · Report: [link]{Markup.Escape(reportPath)}[/]");
    }

    private static string Score(double? score) => score is { } s ? string.Create(CultureInfo.InvariantCulture, $"{s:0.0}%") : "-";
}
