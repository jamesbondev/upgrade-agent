namespace AgentHarness.Policies;

/// <summary>
/// Decides every tool action the agent asks to take. This is where an app's rules live: what runs on its own,
/// what a human approves, and what is refused with feedback the model can act on.
/// Compose policies by wrapping one in another (see <see cref="ToolPolicy.Wrap"/>).
/// </summary>
public interface IToolPolicy
{
    ValueTask<ToolDecision> EvaluateAsync(ToolRequest request, CancellationToken cancellationToken);
}

/// <summary>Ready-made policies and helpers for writing your own.</summary>
public static class ToolPolicy
{
    /// <summary>Everything runs. Only for trusted, sandboxed environments and tests.</summary>
    public static IToolPolicy ApproveAll { get; } = From(_ => ToolDecision.Approve());

    /// <summary>Everything goes to the operator.</summary>
    public static IToolPolicy AskForEverything { get; } = From(r => ToolDecision.Ask($"the agent wants to: {r.Describe()}"));

    /// <summary>Nothing runs: for sessions that should only talk.</summary>
    public static IToolPolicy RejectAll { get; } = From(_ => ToolDecision.Reject("No tools are available in this session; answer from what you already know."));

    public static IToolPolicy From(Func<ToolRequest, ToolDecision> decide) => new DelegatePolicy((r, _) => ValueTask.FromResult(decide(r)));

    public static IToolPolicy From(Func<ToolRequest, CancellationToken, ValueTask<ToolDecision>> decide) => new DelegatePolicy(decide);

    /// <summary>
    /// A policy in front of <paramref name="inner"/>: <paramref name="decide"/> gets the request and the inner
    /// policy's decision, and returns the final one. Use it to add a rule without rewriting the policy under it.
    /// </summary>
    public static IToolPolicy Wrap(this IToolPolicy inner, Func<ToolRequest, ToolDecision, ToolDecision> decide) =>
        new DelegatePolicy(async (r, ct) => decide(r, await inner.EvaluateAsync(r, ct)));

    private sealed class DelegatePolicy(Func<ToolRequest, CancellationToken, ValueTask<ToolDecision>> decide) : IToolPolicy
    {
        public ValueTask<ToolDecision> EvaluateAsync(ToolRequest request, CancellationToken cancellationToken) => decide(request, cancellationToken);
    }
}
