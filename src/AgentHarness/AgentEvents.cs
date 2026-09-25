namespace AgentHarness;

/// <summary>
/// What happens in a session, as data. Backends raise the first four; <see cref="AgentSession"/> adds the rest.
/// Observers (<see cref="IAgentObserver"/>) and stop rules (<see cref="IStopRule"/>) receive all of them, on
/// background threads.
/// </summary>
public abstract record AgentEvent;

public enum ToolKind
{
    Shell,
    Edit,
    Read,
    Other,
}

/// <param name="Detail">The command, path or pattern, as given by the model.</param>
/// <param name="RawArguments">All of the call's arguments as text, whatever the tool.</param>
public sealed record ToolCallStarted(string CallId, ToolKind Kind, string Tool, string Detail, string? RawArguments = null) : AgentEvent;

/// <param name="Output">What the tool returned (for a shell, its output).</param>
public sealed record ToolCallCompleted(string CallId, bool Success, string? Output, string? Error) : AgentEvent
{
    /// <summary>The call this completes. Set by the session, so observers needn't pair events themselves.</summary>
    public ToolCallStarted? Call { get; init; }
}

/// <summary>Text the model wrote between tool calls, and its final reply.</summary>
public sealed record AssistantMessage(string Text) : AgentEvent;

/// <param name="AiCredits">Provider billing units for the call, where the provider reports them.</param>
public sealed record ModelUsage(string? Model, long InputTokens, long OutputTokens, double AiCredits = 0) : AgentEvent;

/// <summary>A message the app sent to the agent (the start of a turn).</summary>
public sealed record UserMessage(string Text) : AgentEvent
{
    /// <summary>A tools-off turn (<see cref="AgentSession.AskAsync{T}(string, CancellationToken)"/>): its reply is data, often JSON, not narration.</summary>
    public bool WithoutTools { get; init; }
}

/// <summary>A tool action was refused, by a policy or by the operator.</summary>
/// <param name="Action">What was asked, with paths relative to the working directory.</param>
/// <param name="Reason">Why, for logs; the model may have been given longer feedback.</param>
/// <param name="CountsTowardLimit">False for nudges that don't count toward <see cref="AgentLimits.MaxRefusals"/>.</param>
public sealed record ToolRefused(ToolRequest Request, string Action, string Reason, bool CountsTowardLimit = true) : AgentEvent
{
    /// <summary>
    /// A policy asked, and the prompter said no (rather than the policy refusing outright). With
    /// <see cref="Policies.ApprovalPrompter.DeclineAll"/> or no console, nobody was actually asked.
    /// </summary>
    public bool DeclinedByOperator { get; init; }
}

/// <summary>The operator approved an action a policy left to a human.</summary>
public sealed record ToolApprovedByOperator(ToolRequest Request, string Action) : AgentEvent;

/// <summary>
/// The model serving the session changed: the first call, or a silent provider fallback. <paramref name="Requested"/>
/// is what was asked for; when the two differ the plan probably doesn't include the requested model.
/// </summary>
public sealed record ModelServed(string Model, string? Requested) : AgentEvent
{
    public bool IsFallback => Requested is not null && !Model.StartsWith(Requested, StringComparison.OrdinalIgnoreCase);
}

/// <summary>A limit or a stop rule ended the session. The first reason wins.</summary>
public sealed record SessionStopped(string Reason) : AgentEvent;
