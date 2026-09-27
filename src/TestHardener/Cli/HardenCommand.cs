using System.CommandLine;
using TestHardener.Config;
using TestHardener.Run;

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
        Description = "Write and check the tests, and save them as a patch. Required until publishing is built.",
    };

    public static Command Create()
    {
        var command = new Command("harden", "Survey each repo, then have an agent write tests that kill the top-ranked surviving mutants, checked with the build, repeated test runs and Stryker.") { Only, From, DryRun };
        command.SetAction((parseResult, cancellationToken) => CommandRunner.RunAsync<HardenCommandHandler>(parseResult, handler => handler.RunAsync(
            parseResult.GetValue(Only) ?? [], parseResult.GetValue(From)?.FullName, parseResult.GetValue(DryRun), cancellationToken)));
        return command;
    }
}

internal sealed class HardenCommandHandler(HardenOrchestrator orchestrator)
{
    public async Task<int> RunAsync(IReadOnlyCollection<string> only, string? from, bool dryRun, CancellationToken cancellationToken)
    {
        if (!dryRun)
        {
            throw new ConfigurationException("Opening pull requests isn't built yet. Run harden with --dry-run to get the tests as a patch.");
        }

        var report = await orchestrator.RunAsync(new HardenArguments(only, from, dryRun), cancellationToken);
        return report.Repos.All(r => r.Status == HardenStatus.Failed) ? ExitCodes.AllReposFailed : ExitCodes.Success;
    }
}
