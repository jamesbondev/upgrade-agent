using System.Collections.Concurrent;
using UpgradeAgent.Agent.Activities;
using UpgradeAgent.Build;
using UpgradeAgent.Infrastructure;

namespace UpgradeAgent.Agent;

/// <summary>
/// Watches a session's events: meters them, turns them into activity (with repository-relative paths),
/// and reads the agent's own build results so a session that stops making progress is stopped.
/// </summary>
internal sealed class SessionMonitor(string worktree, IActivitySink activity, AgentSessionMeter meter, RequiredReading reading, string? requestedModel)
{
    private readonly ConcurrentDictionary<string, ToolCallStarted> _running = new(StringComparer.Ordinal);

    public void OnEvent(AgentEvent agentEvent)
    {
        switch (agentEvent)
        {
            case ToolCallStarted started:
                reading.MarkRead(started.Detail);
                reading.MarkRead(started.RawArguments);
                meter.RecordToolCall();
                AgentTelemetry.ToolCalls.Add(1, new KeyValuePair<string, object?>("gen_ai.tool.name", started.Tool));
                _running[started.CallId] = started;
                activity.Write(new ToolStarted(started.Kind, started.Tool, RepoPath.RelativeInText(worktree, started.Detail)));
                break;

            case ToolCallCompleted completed when _running.TryRemove(completed.CallId, out var started):
                OnCompleted(started, completed);
                break;

            case AssistantMessage message when !meter.SummaryMode:
                if (message.Text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) is { } firstLine)
                {
                    activity.Write(new AgentMessage(firstLine));
                }

                activity.Write(new Transcript("AGENT", message.Text));
                break;

            case ModelUsage usage:
                AgentTelemetry.RecordUsage(usage);
                if (meter.RecordUsage(usage))
                {
                    activity.Write(new Note(requestedModel is not null && !usage.Model!.StartsWith(requestedModel, StringComparison.OrdinalIgnoreCase)
                        ? $"warning: requested model {requestedModel} but the provider is serving {usage.Model} (not available on this plan?)"
                        : $"model: {usage.Model}"));
                }

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
            meter.RecordBuild(output);
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
