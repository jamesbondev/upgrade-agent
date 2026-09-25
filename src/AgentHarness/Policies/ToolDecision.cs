namespace AgentHarness.Policies;

public enum ToolVerdict
{
    /// <summary>Run it.</summary>
    Approve,

    /// <summary>Ask the operator (<see cref="IApprovalPrompter"/>). Unattended runs decline.</summary>
    Ask,

    /// <summary>Refuse it, and tell the model why and what to do instead.</summary>
    Reject,
}

/// <summary>A policy's answer to a <see cref="ToolRequest"/>.</summary>
/// <param name="Reason">
/// For a rejection, this goes back to the model as feedback, so say what to do instead ("Use the edit tool"),
/// not only what is wrong. For an ask, it is shown to the operator.
/// </param>
public sealed record ToolDecision(ToolVerdict Verdict, string Reason)
{
    /// <summary>For an ask: what the operator sees instead of the request's own description.</summary>
    public string? Prompt { get; init; }

    /// <summary>For an ask: what the model is told when the operator declines. Default: find another way.</summary>
    public string? DeclinedFeedback { get; init; }

    /// <summary>
    /// For a rejection: whether it counts toward <see cref="AgentLimits.MaxRefusals"/>. False for nudges
    /// ("read the docs first") that aren't a sign the agent is lost.
    /// </summary>
    public bool CountsTowardRefusalLimit { get; init; } = true;

    /// <summary>For a rejection: what the logs show, when that should be shorter than the feedback to the model.</summary>
    public string? LogReason { get; init; }

    public static ToolDecision Approve(string reason = "approved") => new(ToolVerdict.Approve, reason);

    public static ToolDecision Ask(string reason, string? prompt = null, string? declinedFeedback = null) =>
        new(ToolVerdict.Ask, reason) { Prompt = prompt, DeclinedFeedback = declinedFeedback };

    public static ToolDecision Reject(string feedback) => new(ToolVerdict.Reject, feedback);
}
