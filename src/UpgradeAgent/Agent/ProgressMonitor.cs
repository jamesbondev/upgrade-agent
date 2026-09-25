using AgentHarness;
using UpgradeAgent.Build;

namespace UpgradeAgent.Agent;

internal sealed class ProgressMonitor(int maxBuildsWithoutProgress, int initialErrors) : IStopRule
{
    private int? _lowestErrors = initialErrors > 0 ? initialErrors : null;
    private int _buildsWithoutProgress;

    public string? Check(AgentEvent agentEvent) =>
        agentEvent is ToolCallCompleted { Success: true, Output: { } output, Call: { Kind: ToolKind.Shell } call } && ShellCommands.IsBuild(call.Detail)
            ? RecordBuild(output)
            : null;

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
