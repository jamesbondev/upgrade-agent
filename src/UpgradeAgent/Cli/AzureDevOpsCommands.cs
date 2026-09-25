using System.CommandLine;
using Spectre.Console;
using UpgradeAgent.Config;
using UpgradeAgent.Publishing;

namespace UpgradeAgent.Cli;

internal static class AzureDevOpsCommands
{
    private static readonly Option<bool> Yes = new("--yes") { Description = "Don't ask for confirmation." };

    public static Command Seed()
    {
        var command = new Command("ado-seed", "One-time demo setup: push the target repo's main branch into an EMPTY Azure DevOps repository.");
        command.SetAction((parseResult, cancellationToken) =>
            CommandRunner.RunAsync<AzureDevOpsCommandHandler>(parseResult, handler => handler.SeedAsync(cancellationToken)));
        return command;
    }

    public static Command Cleanup()
    {
        var command = new Command(
            "ado-cleanup",
            "Abandon UpgradeAgent's labelled draft PRs and delete its agent/nuget-updates-* branches in Azure DevOps (demo reset).") { Yes };
        command.SetAction((parseResult, cancellationToken) =>
            CommandRunner.RunAsync<AzureDevOpsCommandHandler>(parseResult, handler => handler.CleanupAsync(parseResult.GetValue(Yes), cancellationToken)));
        return command;
    }
}

internal sealed class AzureDevOpsCommandHandler(ResolvedConfig config, AzureDevOpsPublisher azureDevOps, IAnsiConsole console)
{
    public async Task<int> SeedAsync(CancellationToken cancellationToken)
    {
        console.MarkupLine($"[green]{Markup.Escape(await azureDevOps.SeedAsync(config.RepoPath, "main", cancellationToken))}[/]");
        return ExitCodes.Success;
    }

    public async Task<int> CleanupAsync(bool yes, CancellationToken cancellationToken)
    {
        var plan = await azureDevOps.PlanCleanupAsync(cancellationToken);
        if (plan.IsEmpty)
        {
            console.MarkupLine("[green]Nothing to clean up.[/]");
            return ExitCodes.Success;
        }

        foreach (var pullRequest in plan.PullRequests)
        {
            console.MarkupLine($"  abandon PR [blue]!{pullRequest.PullRequestId}[/] {Markup.Escape(pullRequest.Title)}");
        }

        foreach (var branch in plan.Branches)
        {
            console.MarkupLine($"  delete branch [blue]{Markup.Escape(branch.Name.Replace("refs/heads/", "", StringComparison.Ordinal))}[/]");
        }

        if (!yes && !(console.Profile.Capabilities.Interactive && await console.ConfirmAsync($"Clean up {azureDevOps.Destination}?", defaultValue: false, cancellationToken)))
        {
            console.MarkupLine("[yellow]Nothing changed.[/]");
            return ExitCodes.Success;
        }

        await azureDevOps.ApplyCleanupAsync(plan, cancellationToken);
        console.MarkupLine($"[green]Abandoned {plan.PullRequests.Count} PR(s) and deleted {plan.Branches.Count} branch(es).[/]");
        return ExitCodes.Success;
    }
}
