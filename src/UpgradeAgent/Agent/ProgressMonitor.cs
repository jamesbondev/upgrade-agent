using AgentHarness;
using UpgradeAgent.Build;

namespace UpgradeAgent.Agent;

/// <summary>
/// Stops an agent that is flailing rather than waiting for the tool-call budget: several builds in a row that
/// don't beat the lowest error count so far. A green build resets the count; a limit of 0 disables the check.
/// Refused actions are the harness's job (<see cref="AgentLimits.MaxRefusals"/>). The session calls
/// <see cref="Check"/> one event at a time, so there is no locking here.
/// </summary>
internal sealed class ProgressMonitor(int maxBuildsWithoutProgress, int initialErrors) : IStopRule
{
    private int? _lowestErrors = initialErrors > 0 ? initialErrors : null;
    private int _buildsWithoutProgress;

    /// <summary>Reads the result of every successful <c>dotnet build</c> the agent runs.</summary>
    public string? Check(AgentEvent agentEvent) =>
        agentEvent is ToolCallCompleted { Success: true, Output: { } output, Call: { Kind: ToolKind.Shell } call } && ShellCommands.IsBuild(call.Detail)
            ? RecordBuild(output)
            : null;

    /// <summary>Records one build's output. Output with no recognisable result (e.g. cut by <c>| head</c>) is ignored.</summary>
    public string? RecordBuild(string output)
    {
        if (BuildOutputParser.CountErrors(output) is not { } errors)
        {
            return null;
        }

        if (errors == 0)
        {
            _lowestErrors = null;
            _buildsWithoutProgress = 0;
            return null;
        }

        if (_lowestErrors is null || errors < _lowestErrors)
        {
            _lowestErrors = errors;
            _buildsWithoutProgress = 0;
            return null;
        }

        return ++_buildsWithoutProgress >= maxBuildsWithoutProgress && maxBuildsWithoutProgress > 0
            ? $"agent stopped: {_buildsWithoutProgress} builds without reducing the errors below {_lowestErrors}"
            : null;
    }
}
