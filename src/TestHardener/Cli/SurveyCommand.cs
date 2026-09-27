using System.CommandLine;
using TestHardener.Run;

namespace TestHardener.Cli;

internal static class SurveyCommand
{
    private static readonly Option<string[]> Only = new("--only")
    {
        Description = "Survey only these repos (by Name). Repeatable.",
        AllowMultipleArgumentsPerToken = true,
    };

    private static readonly Option<DirectoryInfo> From = new("--from")
    {
        Description = "An earlier run folder (out/run-...). Reuse its Stryker reports at the same commit instead of running Stryker again.",
    };

    public static Command Create()
    {
        var command = new Command("survey", "Run Stryker on each configured target and report the surviving mutants, grouped by method and ranked. Changes nothing.") { Only, From };
        command.SetAction((parseResult, cancellationToken) => CommandRunner.RunAsync<SurveyCommandHandler>(parseResult, handler => handler.RunAsync(
            parseResult.GetValue(Only) ?? [], parseResult.GetValue(From)?.FullName, cancellationToken)));
        return command;
    }
}

internal sealed class SurveyCommandHandler(SurveyOrchestrator orchestrator)
{
    public async Task<int> RunAsync(IReadOnlyCollection<string> only, string? from, CancellationToken cancellationToken)
    {
        var report = await orchestrator.RunAsync(new SurveyArguments(only, from), cancellationToken);
        return report.Repos.All(r => r.Status == SurveyStatus.Failed) ? ExitCodes.AllReposFailed : ExitCodes.Success;
    }
}
