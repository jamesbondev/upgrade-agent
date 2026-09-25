using AgentHarness.Policies;

namespace AgentHarness.Tests.Policies;

public class ToolPolicyTests
{
    private static readonly ShellRequest Shell = new("ls");

    [Fact]
    public async Task ApproveAllApprovesEverything()
    {
        var decision = await ToolPolicy.ApproveAll.EvaluateAsync(new OtherToolRequest("mcp"), CancellationToken.None);

        Assert.Equal(ToolVerdict.Approve, decision.Verdict);
    }

    [Fact]
    public async Task RejectAllRefusesEverythingWithFeedbackForTheModel()
    {
        var decision = await ToolPolicy.RejectAll.EvaluateAsync(Shell, CancellationToken.None);

        Assert.Equal(ToolVerdict.Reject, decision.Verdict);
        Assert.Contains("No tools", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AskForEverythingAsksWithTheRequestsDescription()
    {
        var decision = await ToolPolicy.AskForEverything.EvaluateAsync(new FileWriteRequest("src/a.cs"), CancellationToken.None);

        Assert.Equal(ToolVerdict.Ask, decision.Verdict);
        Assert.Contains("edit src/a.cs", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FromTurnsAFunctionIntoAPolicy()
    {
        var policy = ToolPolicy.From(request => request is FileReadRequest ? ToolDecision.Approve() : ToolDecision.Reject("Only reads."));

        Assert.Equal(ToolVerdict.Approve, (await policy.EvaluateAsync(new FileReadRequest("a.cs"), CancellationToken.None)).Verdict);
        Assert.Equal(ToolVerdict.Reject, (await policy.EvaluateAsync(Shell, CancellationToken.None)).Verdict);
    }

    [Fact]
    public async Task FromAcceptsAnAsyncFunctionAndPassesTheCancellationToken()
    {
        using var cancellation = new CancellationTokenSource();
        CancellationToken seen = default;
        var policy = ToolPolicy.From((_, ct) =>
        {
            seen = ct;
            return ValueTask.FromResult(ToolDecision.Approve());
        });

        await policy.EvaluateAsync(Shell, cancellation.Token);

        Assert.Equal(cancellation.Token, seen);
    }

    [Fact]
    public async Task WrapCanOverrideTheInnerPolicy()
    {
        var policy = ToolPolicy.RejectAll.Wrap((request, inner) =>
            request is ShellRequest { CommandLine: "make test" } ? ToolDecision.Approve("tests are fine") : inner);

        Assert.Equal(ToolVerdict.Approve, (await policy.EvaluateAsync(new ShellRequest("make test"), CancellationToken.None)).Verdict);
        Assert.Equal(ToolVerdict.Reject, (await policy.EvaluateAsync(new ShellRequest("make deploy"), CancellationToken.None)).Verdict);
    }

    [Fact]
    public async Task WrapSeesTheInnerDecision()
    {
        ToolDecision? seen = null;
        var policy = ToolPolicy.AskForEverything.Wrap((_, inner) => seen = inner);

        await policy.EvaluateAsync(Shell, CancellationToken.None);

        Assert.Equal(ToolVerdict.Ask, seen?.Verdict);
    }
}
