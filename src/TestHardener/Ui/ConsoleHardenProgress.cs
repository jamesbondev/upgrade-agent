using System.Globalization;
using AgentHarness.Policies;
using Spectre.Console;
using TestHardener.Config;
using TestHardener.Hardening;
using TestHardener.Run;

namespace TestHardener.Ui;

internal sealed class SpectreApprovalPrompter(IAnsiConsole console) : IApprovalPrompter
{
    public async Task<bool> ConfirmAsync(string action, string reason, CancellationToken cancellationToken)
    {
        console.MarkupLine($"  [yellow]approval needed:[/] {Markup.Escape(action)} [grey]({Markup.Escape(reason)})[/]");
        try
        {
            return await new ConfirmationPrompt("  Go ahead?") { DefaultValue = false }.ShowAsync(console, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

internal sealed class ConsoleHardenProgress(IAnsiConsole console) : IHardenProgress
{
    public void RepoStarted(RepoTarget target, int index, int count) =>
        console.MarkupLine($"[grey]({index}/{count})[/] {Markup.Escape(target.Name)} [grey]{Markup.Escape(target.Location)}[/]");

    public void GroupStarted(int number, int count, PlannedGroup planned) =>
        console.MarkupLine($"  [grey]group {number}/{count}[/] {Markup.Escape(planned.Group.Member.Name)} [grey]{planned.Group.Survivors.Count} survivors · the agent is writing tests[/]");

    public void GroupFinished(GroupResult result)
    {
        var colour = result.Outcome switch
        {
            GroupOutcome.Verified => "green",
            GroupOutcome.Rejected => "yellow",
            _ => "red",
        };
        var detail = result.Outcome == GroupOutcome.Verified && result.Final is { } final
            ? $"{final.Killed} of {final.Survivors.Count} killed by {final.NewTests.Count} tests"
            : FirstLine(result.Reason ?? "");
        console.MarkupLine($"    [{colour}]{result.Outcome}[/] [grey]{Markup.Escape(detail)} · {result.Rounds.Count} rounds · {result.Duration.TotalMinutes:0.0} min[/]");
    }

    public void RepoFinished(RepoHardenReport report)
    {
        console.MarkupLine($"  {report.Status} [grey]{Markup.Escape(report.PullRequestUrl ?? report.Note ?? report.PatchPath ?? "")}[/]");
        if (report.LeftoverBranches.Count > 0)
        {
            console.MarkupLine($"  [yellow]pushed branches with no pull request:[/] {Markup.Escape(string.Join(", ", report.LeftoverBranches))}");
        }
    }

    public void RunFinished(HardenReport report, string reportPath)
    {
        var table = new Table().Border(TableBorder.Rounded).AddColumns("Repo", "Status", "Groups verified", "Killed", "Tests", "AI credits", "Time");
        foreach (var repo in report.Repos)
        {
            table.AddRow(
                Markup.Escape(repo.Name),
                repo.Status.ToString(),
                $"{repo.Verified} of {repo.Groups.Count}",
                $"{repo.Killed} of {repo.Targeted}",
                repo.TestsAdded.ToString(CultureInfo.InvariantCulture),
                repo.AiCredits.ToString("0.##", CultureInfo.InvariantCulture),
                string.Create(CultureInfo.InvariantCulture, $"{repo.Duration.TotalMinutes:0.0} min"));
        }

        console.WriteLine();
        console.Write(table);
        console.MarkupLine($"{report.Duration.TotalMinutes:0.0} min · Report: [link]{Markup.Escape(reportPath)}[/]");
    }

    private static string FirstLine(string text)
    {
        var line = text.ReplaceLineEndings("\n").Split('\n')[0];
        return line.Length > 120 ? line[..120] + "…" : line;
    }
}
