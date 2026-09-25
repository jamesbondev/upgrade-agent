using System.CommandLine;
using AgentHarness.Policies;
using ReadmeChecker.Run;
using ReadmeChecker.Ui;
using Spectre.Console;

namespace ReadmeChecker.Cli;

internal static class FixCommand
{
    private static readonly Option<string[]> Only = new("--only")
    {
        Description = "Fix only these repos (by Name). Repeatable.",
        AllowMultipleArgumentsPerToken = true,
    };

    private static readonly Option<bool> Yes = new("--yes")
    {
        Description = "Push and open each draft pull request without asking.",
    };

    private static readonly Option<bool> DryRun = new("--dry-run")
    {
        Description = "Fix and check each README, and save the change as a patch, but push nothing.",
    };

    public static Command Create()
    {
        var command = new Command("fix", "Check each repo; for a stale README, have the agent fix it, check the fix, and open a draft pull request.") { Only, Yes, DryRun, CheckCommand.Deep };
        command.SetAction((parseResult, cancellationToken) => CommandRunner.RunAsync<FixCommandHandler>(parseResult, handler => handler.RunAsync(
            parseResult.GetValue(Only) ?? [], parseResult.GetValue(Yes), parseResult.GetValue(DryRun), parseResult.GetValue(CheckCommand.Deep), cancellationToken)));
        return command;
    }
}

internal sealed class FixCommandHandler(FixOrchestrator orchestrator, IAnsiConsole console)
{
    public async Task<int> RunAsync(IReadOnlyCollection<string> only, bool yes, bool dryRun, bool deep, CancellationToken cancellationToken)
    {
        var prompter = yes ? ApprovalPrompter.From((_, _) => true)
            : console.Profile.Capabilities.Interactive && !Console.IsInputRedirected ? new SpectreApprovalPrompter(console)
            : ApprovalPrompter.DeclineAll;

        var report = await orchestrator.RunAsync(new FixArguments(only, dryRun, prompter, deep), cancellationToken);
        return report.Repos.All(r => r.Status == FixStatus.Failed || r.Check.Verdict == RepoVerdict.Error) ? ExitCodes.AllReposFailed : ExitCodes.Success;
    }
}
