using AgentHarness.Policies;
using AgentHarness.Testing;
using AgentHarness.Tests.TestSupport;

namespace AgentHarness.Tests;

/// <summary>What observers see, and what <see cref="AgentSession.Stats"/> counts.</summary>
public sealed class AgentSessionEventTests : IDisposable
{
    private readonly TempDirectory _workspace = new();
    private readonly EventRecorder _events = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task EachTurnStartsWithTheUserMessage()
    {
        var backend = new ScriptedBackend().Reply("done");
        var runner = new AgentRunner(backend);

        await runner.RunAsync(Options(ToolPolicy.ApproveAll), "Fix the build.");

        Assert.Equal(new UserMessage("Fix the build."), _events.Events[0]);
    }

    [Fact]
    public async Task ToolCallsAndRepliesReachObserversInOrder()
    {
        var backend = new ScriptedBackend().Turn(t => t.Say("Looking.").Shell("ls", output: "src").Reply("done"));
        var runner = new AgentRunner(backend);

        await runner.RunAsync(Options(ToolPolicy.ApproveAll), "Look around.");

        Assert.Collection(
            _events.Events,
            e => Assert.IsType<UserMessage>(e),
            e => Assert.Equal(new AssistantMessage("Looking."), e),
            e => Assert.Equal("ls", Assert.IsType<ToolCallStarted>(e).Detail),
            e => Assert.Equal("src", Assert.IsType<ToolCallCompleted>(e).Output),
            e => Assert.Equal(new AssistantMessage("done"), e));
    }

    [Fact]
    public async Task ACompletedToolCallCarriesTheCallItCompletes()
    {
        var backend = new ScriptedBackend().Turn(t => t.Shell("ls").Shell("pwd").Reply("done"));
        var runner = new AgentRunner(backend);

        await runner.RunAsync(Options(ToolPolicy.ApproveAll), "Look around.");

        var started = _events.OfType<ToolCallStarted>();
        var completed = _events.OfType<ToolCallCompleted>();
        Assert.Same(started[0], completed[0].Call);
        Assert.Same(started[1], completed[1].Call);
    }

    [Fact]
    public async Task TheFirstModelUsageReportsTheServedModel()
    {
        var backend = new ScriptedBackend("claude-sonnet-4.5").Turn(t => t.Usage(10, 5, "claude-sonnet-4.5").Usage(10, 5, "claude-sonnet-4.5").Reply("done"));
        var runner = new AgentRunner(backend);

        await runner.RunAsync(Options(ToolPolicy.ApproveAll), "Go.");

        var served = Assert.Single(_events.OfType<ModelServed>());
        Assert.Equal(new ModelServed("claude-sonnet-4.5", "claude-sonnet-4.5"), served);
        Assert.False(served.IsFallback);
    }

    [Fact]
    public async Task ASilentProviderFallbackIsReportedAsAFallback()
    {
        var backend = new ScriptedBackend("claude-sonnet-4.5").Turn(t => t.Usage(10, 5, "claude-sonnet-4.5").Usage(10, 5, "gpt-4.1").Reply("done"));
        var runner = new AgentRunner(backend);

        var result = await runner.RunAsync(Options(ToolPolicy.ApproveAll), "Go.");

        var fallback = _events.OfType<ModelServed>()[^1];
        Assert.True(fallback.IsFallback);
        Assert.Equal("gpt-4.1", fallback.Model);
        Assert.Equal("gpt-4.1", result.Stats.Model);
    }

    [Fact]
    public async Task WhenTheProviderChoosesTheModelNothingIsAFallback()
    {
        var backend = new ScriptedBackend(model: null).Turn(t => t.Usage(10, 5, "gpt-4.1").Reply("done"));
        var runner = new AgentRunner(backend);

        await runner.RunAsync(Options(ToolPolicy.ApproveAll), "Go.");

        Assert.False(Assert.Single(_events.OfType<ModelServed>()).IsFallback);
    }

    [Fact]
    public async Task StatsCountModelCallsAndTokens()
    {
        var backend = new ScriptedBackend().Turn(t => t.Usage(100, 20).Usage(50, 10).Reply("done"));
        var runner = new AgentRunner(backend);

        var result = await runner.RunAsync(Options(ToolPolicy.ApproveAll), "Go.");

        Assert.Equal(2, result.Stats.ModelCalls);
        Assert.Equal(150, result.Stats.InputTokens);
        Assert.Equal(30, result.Stats.OutputTokens);
        Assert.Equal("scripted-model", result.Stats.Model);
    }

    [Fact]
    public async Task StatsCountToolCallsThatRanAndRefusalsSeparately()
    {
        var backend = new ScriptedBackend().Turn(t => t.Shell("ls").Shell("pwd").Shell("curl x").Reply("done"));
        var runner = new AgentRunner(backend);

        var result = await runner.RunAsync(Options(new WorkspacePolicy(_workspace.Path)), "Go.");

        Assert.Equal(2, result.Stats.ToolCalls);
        Assert.Equal(1, result.Stats.Refusals);
        Assert.Null(result.Stats.StopReason);
    }

    [Fact]
    public async Task StatsCountOperatorApprovals()
    {
        var backend = new ScriptedBackend().Turn(t => t.Shell("a").Shell("b").Reply("done"));
        var runner = new AgentRunner(backend, ApprovalPrompter.From((_, _) => true));

        var result = await runner.RunAsync(Options(ToolPolicy.AskForEverything), "Go.");

        Assert.Equal(2, result.Stats.OperatorApprovals);
        Assert.Equal(2, _events.OfType<ToolApprovedByOperator>().Count);
    }

    [Fact]
    public async Task StatsRecordTheStopReason()
    {
        var backend = new ScriptedBackend().Turn(t => t.Shell("a").Shell("b").Reply("done"));
        var runner = new AgentRunner(backend);

        var result = await runner.RunAsync(Options(ToolPolicy.ApproveAll, new AgentLimits { MaxToolCalls = 1 }), "Go.");

        Assert.Equal("agent stopped: more than 1 tool calls", result.Stats.StopReason);
    }

    [Fact]
    public async Task UsageWithoutAModelReportsTheBackendsModel()
    {
        var backend = new ScriptedBackend("gpt-5").Turn(t => t.Usage(10, 5).Reply("done"));
        var runner = new AgentRunner(backend);

        await runner.RunAsync(Options(ToolPolicy.ApproveAll), "Go.");

        Assert.False(Assert.Single(_events.OfType<ModelServed>()).IsFallback);
    }

    private AgentSessionOptions Options(IToolPolicy policy, AgentLimits? limits = null) => new()
    {
        WorkingDirectory = _workspace.Path,
        Policy = policy,
        Limits = limits ?? AgentLimits.None,
        Observers = [_events],
    };
}
