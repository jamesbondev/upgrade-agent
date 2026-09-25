namespace AgentHarness;

public abstract record AgentEvent;

public enum ToolKind
{
    Shell,
    Edit,
    Read,
    Other,
}

public sealed record ToolCallStarted(string CallId, ToolKind Kind, string Tool, string Detail, string? RawArguments = null) : AgentEvent;

public sealed record ToolCallCompleted(string CallId, bool Success, string? Output, string? Error) : AgentEvent
{
    public ToolCallStarted? Call { get; init; }
}

public sealed record AssistantMessage(string Text) : AgentEvent;

public sealed record ModelUsage(string? Model, long InputTokens, long OutputTokens, double AiCredits = 0) : AgentEvent;

public sealed record UserMessage(string Text) : AgentEvent
{
    public bool WithoutTools { get; init; }
}

public sealed record ToolRefused(ToolRequest Request, string Action, string Reason, bool CountsTowardLimit = true) : AgentEvent
{
    public bool DeclinedByOperator { get; init; }
}

public sealed record ToolApprovedByOperator(ToolRequest Request, string Action) : AgentEvent;

public sealed record ModelServed(string Model, string? Requested) : AgentEvent
{
    public bool IsFallback => Requested is not null && !Model.StartsWith(Requested, StringComparison.OrdinalIgnoreCase);
}

public sealed record SessionStopped(string Reason) : AgentEvent;
