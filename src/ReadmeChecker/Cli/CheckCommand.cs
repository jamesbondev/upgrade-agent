using System.CommandLine;
using ReadmeChecker.Config;
using ReadmeChecker.Run;

namespace ReadmeChecker.Cli;

internal static class CommonOptions
{
    public static readonly Option<FileInfo> Config = new("--config")
    {
        Description = "JSON config file layered over appsettings.json.",
        Recursive = true,
    };

    public static readonly Option<bool> Verbose = new("--verbose")
    {
        Description = "Trace every external command (git) to stderr.",
        Recursive = true,
    };
}

internal static class CheckCommand
{
    private static readonly Option<string[]> Only = new("--only")
    {
        Description = "Check only these repos (by Name). Repeatable.",
        AllowMultipleArgumentsPerToken = true,
    };

    private static readonly Option<AgentProvider?> Agent = new("--agent")
    {
        Description = "copilot, or none for the script's signals only. Default: Agent:Provider.",
    };

    public static readonly Option<bool> Deep = new("--deep")
    {
        Description = "Check the README claim by claim, one part at a time (several agent sessions per repo). Default: Readme:Depth.",
    };

    public static Command Create()
    {
        var command = new Command("check", "Clone each configured repo and report whether its README is out of date. Changes nothing.") { Only, Agent, Deep };
        command.SetAction((parseResult, cancellationToken) => CommandRunner.RunAsync<CheckCommandHandler>(parseResult, handler => handler.RunAsync(
            parseResult.GetValue(Only) ?? [], parseResult.GetValue(Agent), parseResult.GetValue(Deep), cancellationToken)));
        return command;
    }
}

internal sealed class CheckCommandHandler(ResolvedConfig config, CheckOrchestrator orchestrator)
{
    public async Task<int> RunAsync(IReadOnlyCollection<string> only, AgentProvider? agent, bool deep, CancellationToken cancellationToken)
    {
        var report = await orchestrator.RunAsync(new CheckArguments(only, agent ?? config.Options.Agent.Provider, deep), cancellationToken);
        return report.Repos.All(r => r.Verdict == RepoVerdict.Error) ? ExitCodes.AllReposFailed : ExitCodes.Success;
    }
}
