using System.CommandLine;
using AgentHarness.Policies;
using Spectre.Console;
using TestHardener.Run;
using TestHardener.Ui;

namespace TestHardener.Cli;

internal static class HardenCommand
{
    private static readonly Option<string[]> Only = new("--only")
    {
        Description = "Harden only these repos (by Name). Repeatable.",
        AllowMultipleArgumentsPerToken = true,
    };

    private static readonly Option<DirectoryInfo> From = new("--from")
    {
        Description = "An earlier run folder (out/run-...). Reuse its Stryker reports at the same commit instead of running Stryker again.",
    };

    private static readonly Option<bool> DryRun = new("--dry-run")
    {
        Description = "Write and check the tests, and save them as a patch, but push nothing.",
    };

    private static readonly Option<bool> Yes = new("--yes")
    {
        Description = "Push and open each draft pull request without asking.",
    };

    public static Command Create()
    {
        var command = new Command("harden", "Survey each repo, then have an agent write tests that kill the top-ranked surviving mutants, checked with the build, repeated test runs and Stryker, and open a draft pull request.") { Only, From, DryRun, Yes };
        command.SetAction((parseResult, cancellationToken) => CommandRunner.RunAsync<HardenCommandHandler>(parseResult, handler => handler.RunAsync(
            parseResult.GetValue(Only) ?? [], parseResult.GetValue(From)?.FullName, parseResult.GetValue(DryRun), parseResult.GetValue(Yes), cancellationToken)));
        return command;
    }
}

internal sealed class HardenCommandHandler(HardenOrchestrator orchestrator, IAnsiConsole console)
{
    public async Task<int> RunAsync(IReadOnlyCollection<string> only, string? from, bool dryRun, bool yes, CancellationToken cancellationToken)
    {
        var prompter = yes ? ApprovalPrompter.From((_, _) => true)
            : console.Profile.Capabilities.Interactive && !Console.IsInputRedirected ? new SpectreApprovalPrompter(console)
            : ApprovalPrompter.DeclineAll;
        var report = await orchestrator.RunAsync(new HardenArguments(only, from, dryRun, prompter), cancellationToken);
        return report.Repos.All(r => r.Status == HardenStatus.Failed) ? ExitCodes.AllReposFailed : ExitCodes.Success;
    }
}
