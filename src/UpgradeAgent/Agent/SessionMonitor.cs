using AgentHarness;
using UpgradeAgent.Agent.Activities;
using UpgradeAgent.Build;
using UpgradeAgent.Infrastructure;

namespace UpgradeAgent.Agent;

internal sealed class SessionMonitor(string worktree, IActivitySink activity, RequiredReading reading) : IAgentObserver
{
    private volatile bool _summaryMode;

    public bool SummaryMode
    {
        get => _summaryMode;
        set => _summaryMode = value;
    }

    public void OnEvent(AgentEvent agentEvent)
    {
        switch (agentEvent)
        {
            case ToolCallStarted started:
                reading.MarkRead(started.Detail);
                reading.MarkRead(started.RawArguments);
                activity.Write(new ToolStarted(started.Kind, started.Tool, RepoPath.RelativeInText(worktree, started.Detail)));
                break;

            case ToolCallCompleted { Call: { } started } completed:
                OnCompleted(started, completed);
                break;

            case AssistantMessage message when !SummaryMode:
                if (message.Text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) is { } firstLine)
                {
                    activity.Write(new AgentMessage(firstLine));
                }

                activity.Write(new Transcript("AGENT", message.Text));
                break;

            case ModelServed served:
                activity.Write(new Note(served.IsFallback
                    ? $"warning: requested model {served.Requested} but the provider is serving {served.Model} (not available on this plan?)"
                    : $"model: {served.Model}"));
                break;

            case ToolRefused refused:
                activity.Write(new ActionRefused(RepoPath.RelativeInText(worktree, refused.Action), refused.Reason));
                break;

            case SessionStopped stopped:
                activity.Write(new Note(stopped.Reason));
                break;
        }
    }

    private void OnCompleted(ToolCallStarted started, ToolCallCompleted completed)
    {
        if (!completed.Success)
        {
            activity.Write(new ToolFailed(completed.Error ?? "failed"));
            return;
        }

        if (started.Kind != ToolKind.Shell || completed.Output is not { } output)
        {
            return;
        }

        if (ShellCommands.IsBuild(started.Detail))
        {
            if (BuildOutputParser.CountErrors(output) is { } errors)
            {
                activity.Write(new BuildChecked(errors, BuildOutputParser.TopCodes(BuildOutputParser.ParseDiagnostics(output).Errors, 4)));
            }
        }
        else if (ShellCommands.IsTest(started.Detail) && BuildOutputParser.ParseTestCounts(output) is { } counts)
        {
            activity.Write(new TestsChecked(counts.Passed, counts.Failed));
        }
    }
}

internal static class ShellCommands
{
    public static bool IsBuild(string command) => command.Contains("dotnet build", StringComparison.OrdinalIgnoreCase);

    public static bool IsTest(string command) => command.Contains("dotnet test", StringComparison.OrdinalIgnoreCase);
}
