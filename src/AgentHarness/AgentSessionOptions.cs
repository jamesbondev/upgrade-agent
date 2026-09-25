using AgentHarness.Policies;

namespace AgentHarness;

public sealed class AgentSessionOptions
{
    public string Name { get; init; } = "agent";

    public required string WorkingDirectory { get; init; }

    public string Instructions { get; init; } = "";

    public required IToolPolicy Policy { get; init; }

    public IReadOnlyList<AgentTool> Tools { get; init; } = [];

    public bool UseBuiltInTools { get; init; } = true;

    public bool AllowWebFetch { get; init; }

    public AgentLimits Limits { get; init; } = new();

    public IReadOnlyList<IAgentObserver> Observers { get; init; } = [];

    public IReadOnlyList<IStopRule> StopRules { get; init; } = [];
}

public sealed record AgentLimits
{
    public TimeSpan? MaxDuration { get; init; } = TimeSpan.FromMinutes(10);

    public int MaxToolCalls { get; init; } = 80;

    public int MaxRefusals { get; init; } = 5;

    public static AgentLimits None { get; } = new() { MaxDuration = null, MaxToolCalls = 0, MaxRefusals = 0 };
}

public interface IAgentObserver
{
    void OnEvent(AgentEvent agentEvent);
}

public interface IStopRule
{
    string? Check(AgentEvent agentEvent);
}

public static class AgentObserver
{
    public static IAgentObserver From(Action<AgentEvent> onEvent) => new DelegateObserver(onEvent);

    public static IStopRule StopWhen(Func<AgentEvent, string?> check) => new DelegateStopRule(check);

    private sealed class DelegateObserver(Action<AgentEvent> onEvent) : IAgentObserver
    {
        public void OnEvent(AgentEvent agentEvent) => onEvent(agentEvent);
    }

    private sealed class DelegateStopRule(Func<AgentEvent, string?> check) : IStopRule
    {
        public string? Check(AgentEvent agentEvent) => check(agentEvent);
    }
}

public sealed record AgentReply(string? Text, string? StopReason)
{
    public bool Stopped => StopReason is not null;
}

public sealed record AgentStats(
    string? Model,
    int ModelCalls,
    int ToolCalls,
    long InputTokens,
    long OutputTokens,
    double AiCredits,
    int OperatorApprovals,
    int Refusals,
    string? StopReason,
    TimeSpan Duration)
{
    public override string ToString() =>
        $"{Duration.TotalMinutes:0.0} min · {ModelCalls} model calls · {ToolCalls} tool calls · {Tokens(InputTokens)} in / {Tokens(OutputTokens)} out tokens"
        + $" · {Refusals} refused · {OperatorApprovals} approved by the operator{(StopReason is null ? "" : $" · {StopReason}")}";

    private static string Tokens(long count) => count < 1000 ? $"{count}" : $"{count / 1000}k";
}
