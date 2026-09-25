using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace UpgradeAgent.Agent;

/// <summary>
/// Spans and metrics for agent sessions, named after the OpenTelemetry GenAI semantic conventions. Nothing is
/// exported by default: attach any listener (an OpenTelemetry SDK, <c>dotnet-counters monitor UpgradeAgent.Agent</c>,
/// <c>dotnet-trace</c>) to see them.
/// </summary>
internal static class AgentTelemetry
{
    public const string SourceName = "UpgradeAgent.Agent";

    public static readonly ActivitySource Source = new(SourceName);

    private static readonly Meter Meter = new(SourceName);

    private static readonly Counter<long> Tokens = Meter.CreateCounter<long>("gen_ai.client.token.usage", "{token}", "Tokens used by the agent.");

    public static readonly Counter<long> ToolCalls = Meter.CreateCounter<long>("upgrade_agent.tool_calls", "{call}", "Tool calls the agent made.");

    /// <summary>An <c>invoke_agent</c> span for one group's session.</summary>
    public static Activity? StartSession(string provider, string group, string? model) =>
        Source.StartActivity($"invoke_agent {group}", ActivityKind.Client)?
            .SetTag("gen_ai.operation.name", "invoke_agent")
            .SetTag("gen_ai.provider.name", provider)
            .SetTag("gen_ai.request.model", model)
            .SetTag("upgrade_agent.group", group);

    public static void RecordUsage(ModelUsage usage)
    {
        KeyValuePair<string, object?> model = new("gen_ai.response.model", usage.Model);
        Tokens.Add(usage.InputTokens, model, new("gen_ai.token.type", "input"));
        Tokens.Add(usage.OutputTokens, model, new("gen_ai.token.type", "output"));
    }
}
