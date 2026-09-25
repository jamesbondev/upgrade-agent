namespace AgentHarness.Policies;

public enum ToolVerdict
{
    Approve,

    Ask,

    Reject,
}

public sealed record ToolDecision(ToolVerdict Verdict, string Reason)
{
    public string? Prompt { get; init; }

    public string? DeclinedFeedback { get; init; }

    public bool CountsTowardRefusalLimit { get; init; } = true;

    public string? LogReason { get; init; }

    public static ToolDecision Approve(string reason = "approved") => new(ToolVerdict.Approve, reason);

    public static ToolDecision Ask(string reason, string? prompt = null, string? declinedFeedback = null) =>
        new(ToolVerdict.Ask, reason) { Prompt = prompt, DeclinedFeedback = declinedFeedback };

    public static ToolDecision Reject(string feedback) => new(ToolVerdict.Reject, feedback);
}
