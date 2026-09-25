using AgentHarness.Policies;

namespace AgentHarness;

/// <summary>Everything one session needs. Only <see cref="WorkingDirectory"/> and <see cref="Policy"/> are required.</summary>
public sealed class AgentSessionOptions
{
    /// <summary>A name for logs and telemetry spans, e.g. the task or work item being handled.</summary>
    public string Name { get; init; } = "agent";

    /// <summary>The folder the agent works in. Its shell starts here; policies usually confine it here.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>Your system prompt: the role, the rules, how to verify its work. Appended to the provider's own.</summary>
    public string Instructions { get; init; } = "";

    /// <summary>Decides every tool action. Start from <see cref="WorkspacePolicy"/>; see <see cref="ToolPolicy"/> for others.</summary>
    public required IToolPolicy Policy { get; init; }

    /// <summary>Your own tools, offered next to the runtime's shell and file tools.</summary>
    public IReadOnlyList<AgentTool> Tools { get; init; } = [];

    /// <summary>False offers only <see cref="Tools"/>: no shell and no file tools. Use it for narrow sessions (publish, classify).</summary>
    public bool UseBuiltInTools { get; init; } = true;

    /// <summary>Offer the runtime's web fetch tool. Off by default: fetched pages are untrusted input.</summary>
    public bool AllowWebFetch { get; init; }

    public AgentLimits Limits { get; init; } = new();

    /// <summary>See every event: console output, log files, metrics. See <see cref="ConsoleAgentObserver"/>.</summary>
    public IReadOnlyList<IAgentObserver> Observers { get; init; } = [];

    /// <summary>Your own reasons to stop early, e.g. "three builds without fewer errors".</summary>
    public IReadOnlyList<IStopRule> StopRules { get; init; } = [];
}

/// <summary>
/// Budgets for one session. A limit of 0 (or null) disables it. When one is hit, the session is stopped and
/// the turn returns with <see cref="AgentReply.StopReason"/> set.
/// </summary>
public sealed record AgentLimits
{
    /// <summary>
    /// Time the agent spends working: starting the session and in <see cref="AgentSession.SendAsync"/> turns. Time
    /// between turns and time waiting on the operator don't count.
    /// </summary>
    public TimeSpan? MaxDuration { get; init; } = TimeSpan.FromMinutes(10);

    public int MaxToolCalls { get; init; } = 80;

    /// <summary>Refused actions before the agent is judged lost. Refusals marked as nudges don't count.</summary>
    public int MaxRefusals { get; init; } = 5;

    /// <summary>No limits at all. For tests and trusted, supervised use.</summary>
    public static AgentLimits None { get; } = new() { MaxDuration = null, MaxToolCalls = 0, MaxRefusals = 0 };
}

/// <summary>Receives every <see cref="AgentEvent"/> of a session, on background threads (calls are serialised per session).</summary>
public interface IAgentObserver
{
    void OnEvent(AgentEvent agentEvent);
}

/// <summary>
/// A reason to stop a session early that the built-in <see cref="AgentLimits"/> don't cover. It sees every event;
/// returning a reason stops the session (the first reason wins). Calls are serialised per session.
/// </summary>
public interface IStopRule
{
    string? Check(AgentEvent agentEvent);
}

/// <summary>Helpers for one-off observers and stop rules.</summary>
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

/// <summary>A finished turn.</summary>
/// <param name="Text">The model's final reply; null when the turn was stopped.</param>
/// <param name="StopReason">Why the session was stopped (a limit or a stop rule), or null when the turn finished normally.</param>
public sealed record AgentReply(string? Text, string? StopReason)
{
    public bool Stopped => StopReason is not null;
}

/// <summary>What a session did, so far.</summary>
/// <param name="Model">The model that actually served the last call (providers fall back silently).</param>
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
        $"{Duration.TotalMinutes:0.0} min · {ModelCalls} model calls · {ToolCalls} tool calls · {InputTokens / 1000}k in / {OutputTokens / 1000}k out tokens";
}
