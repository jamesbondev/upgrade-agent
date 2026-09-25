using AgentHarness.Policies;
using ReadmeChecker.Config;
using ReadmeChecker.Run;
using Spectre.Console;

namespace ReadmeChecker.Ui;

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

internal sealed class ConsoleFixProgress(IAnsiConsole console) : IFixProgress
{
    public void RepoStarted(RepoTarget target, int index, int count) =>
        console.MarkupLine($"[grey]({index}/{count})[/] {Markup.Escape(target.Name)} [grey]{Markup.Escape(target.Location)}[/]");

    public void RepoFinished(RepoFixReport report)
    {
        var detail = report.PullRequestUrl ?? report.Reason ?? "";
        console.MarkupLine($"  {Colour(report.Status)} [grey]{Markup.Escape(detail)} · {report.Duration.TotalSeconds:0}s[/]");
        foreach (var problem in report.Problems)
        {
            console.MarkupLine($"    [red]-[/] {Markup.Escape(problem)}");
        }
    }

    public void RunFinished(FixRunReport report, string reportPath)
    {
        console.WriteLine();
        console.MarkupLine($"{report.Repos.Count(r => r.Status == FixStatus.Opened)} pull request(s) opened · AI credits: {report.AiCredits:0.##} · {report.Duration.TotalMinutes:0.0} min");
        console.MarkupLine($"Report: [link]{Markup.Escape(reportPath)}[/]");
    }

    private static string Colour(FixStatus status) => status switch
    {
        FixStatus.Opened => "[green]Opened[/]",
        FixStatus.Ready => "[blue]Ready[/]",
        FixStatus.Rejected or FixStatus.Failed => $"[red]{status}[/]",
        FixStatus.Declined or FixStatus.Skipped => $"[yellow]{status}[/]",
        _ => $"[grey]{status}[/]",
    };
}
