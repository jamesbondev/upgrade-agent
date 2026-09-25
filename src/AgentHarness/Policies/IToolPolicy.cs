namespace AgentHarness.Policies;

public interface IToolPolicy
{
    ValueTask<ToolDecision> EvaluateAsync(ToolRequest request, CancellationToken cancellationToken);
}

public static class ToolPolicy
{
    public static IToolPolicy ApproveAll { get; } = From(_ => ToolDecision.Approve());

    public static IToolPolicy AskForEverything { get; } = From(r => ToolDecision.Ask($"the agent wants to: {r.Describe()}"));

    public static IToolPolicy RejectAll { get; } = From(_ => ToolDecision.Reject("No tools are available in this session; answer from what you already know."));

    public static IToolPolicy From(Func<ToolRequest, ToolDecision> decide) => new DelegatePolicy((r, _) => ValueTask.FromResult(decide(r)));

    public static IToolPolicy From(Func<ToolRequest, CancellationToken, ValueTask<ToolDecision>> decide) => new DelegatePolicy(decide);

    public static IToolPolicy Wrap(this IToolPolicy inner, Func<ToolRequest, ToolDecision, ToolDecision> decide) =>
        new DelegatePolicy(async (r, ct) => decide(r, await inner.EvaluateAsync(r, ct)));

    private sealed class DelegatePolicy(Func<ToolRequest, CancellationToken, ValueTask<ToolDecision>> decide) : IToolPolicy
    {
        public ValueTask<ToolDecision> EvaluateAsync(ToolRequest request, CancellationToken cancellationToken) => decide(request, cancellationToken);
    }
}
