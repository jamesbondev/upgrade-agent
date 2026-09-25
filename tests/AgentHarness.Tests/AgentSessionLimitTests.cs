using AgentHarness.Policies;
using AgentHarness.Testing;
using AgentHarness.Tests.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace AgentHarness.Tests;

public sealed class AgentSessionLimitTests : IDisposable
{
    private static readonly TimeSpan TenMinutes = TimeSpan.FromMinutes(10);

    private readonly TempDirectory _workspace = new();
    private readonly EventRecorder _events = new();
    private readonly FakeTimeProvider _time = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task MaxToolCallsStopsTheSessionAtTheFirstCallOverTheLimit()
    {
        var backend = new ScriptedBackend().Turn(t => t.Shell("ls").Shell("ls").Shell("ls").Shell("ls").Reply("done"));
        var runner = new AgentRunner(backend);

        var result = await runner.RunAsync(Options(ToolPolicy.ApproveAll, new AgentLimits { MaxToolCalls = 2 }), "Look around.");

        Assert.True(result.Reply.Stopped);
        Assert.Null(result.Reply.Text);
        Assert.Equal("agent stopped: more than 2 tool calls", result.Reply.StopReason);
        Assert.Equal(3, result.Stats.ToolCalls);
    }

    [Fact]
    public async Task MaxRefusalsStopsTheSessionAtTheFirstRefusalOverTheLimit()
    {
        var backend = new ScriptedBackend().Turn(t => t.Shell("curl a").Shell("curl b").Shell("curl c").Shell("curl d").Reply("done"));
        var runner = new AgentRunner(backend);

        var result = await runner.RunAsync(Options(new WorkspacePolicy(_workspace.Path), new AgentLimits { MaxRefusals = 2 }), "Download it.");

        Assert.Equal("agent stopped: more than 2 refused actions", result.Reply.StopReason);
        Assert.Equal(3, backend.Decisions.Count);
    }

    [Fact]
    public async Task RefusalsThatDoNotCountTowardTheLimitNeverStopTheSession()
    {
        var nudge = ToolPolicy.From(_ => ToolDecision.Reject("Read the migration guide first.") with { CountsTowardRefusalLimit = false });
        var backend = new ScriptedBackend().Turn(t => t.Shell("a").Shell("b").Shell("c").Reply("done"));
        var runner = new AgentRunner(backend);

        var result = await runner.RunAsync(Options(nudge, new AgentLimits { MaxRefusals = 1 }), "Go.");

        Assert.False(result.Reply.Stopped);
        Assert.Equal(3, result.Stats.Refusals);
        Assert.All(_events.OfType<ToolRefused>(), r => Assert.False(r.CountsTowardLimit));
    }

    [Fact]
    public async Task OperatorDeclinesCountTowardTheRefusalLimit()
    {
        var backend = new ScriptedBackend().Turn(t => t.Shell("a").Shell("b").Reply("done"));
        var runner = new AgentRunner(backend, ApprovalPrompter.DeclineAll);

        var result = await runner.RunAsync(Options(ToolPolicy.AskForEverything, new AgentLimits { MaxRefusals = 1 }), "Go.");

        Assert.Equal("agent stopped: more than 1 refused actions", result.Reply.StopReason);
    }

    [Fact]
    public async Task MaxDurationStopsATurnThatRunsTooLong()
    {
        var backend = new ScriptedBackend().Turn(t => t.Wait(TimeSpan.FromSeconds(30)).Reply("done"));
        var runner = new AgentRunner(backend);

        var result = await runner.RunAsync(Options(ToolPolicy.ApproveAll, new AgentLimits { MaxDuration = TimeSpan.FromMilliseconds(100) }), "Go.");

        Assert.Equal("agent stopped: time budget exceeded", result.Reply.StopReason);
        Assert.True(result.Stats.Duration < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task MaxDurationCountsTimeSpentWorking()
    {
        var backend = new ScriptedBackend().Turn(t => t.Elapse(_time, TenMinutes).Wait(TimeSpan.FromMinutes(1)).Reply("done"));
        var runner = new AgentRunner(backend, time: _time);

        var result = await runner.RunAsync(Options(ToolPolicy.ApproveAll, new AgentLimits { MaxDuration = TenMinutes }), "Go.");

        Assert.Equal("agent stopped: time budget exceeded", result.Reply.StopReason);
    }

    [Fact]
    public async Task MaxDurationAddsUpTimeAcrossTurns()
    {
        var backend = new ScriptedBackend()
            .Turn(t => t.Elapse(_time, TimeSpan.FromMinutes(6)).Reply("first"))
            .Turn(t => t.Elapse(_time, TimeSpan.FromMinutes(6)).Reply("second"));
        var runner = new AgentRunner(backend, time: _time);
        await using var session = await runner.StartAsync(Options(ToolPolicy.ApproveAll, new AgentLimits { MaxDuration = TenMinutes }));

        var first = await session.SendAsync("One.");
        var second = await session.SendAsync("Two.");

        Assert.False(first.Stopped);
        Assert.True(second.Stopped);
    }

    [Fact]
    public async Task TimeBetweenTurnsDoesNotCountTowardMaxDuration()
    {
        var backend = new ScriptedBackend().Reply("first").Reply("second");
        var runner = new AgentRunner(backend, time: _time);
        await using var session = await runner.StartAsync(Options(ToolPolicy.ApproveAll, new AgentLimits { MaxDuration = TenMinutes }));

        await session.SendAsync("One.");
        _time.Advance(TimeSpan.FromHours(1));
        var reply = await session.SendAsync("Two.");

        Assert.False(reply.Stopped);
        Assert.Equal("second", reply.Text);
    }

    [Fact]
    public async Task TimeBeforeTheFirstTurnDoesNotCountTowardMaxDuration()
    {
        var backend = new ScriptedBackend().Reply("done");
        var runner = new AgentRunner(backend, time: _time);
        await using var session = await runner.StartAsync(Options(ToolPolicy.ApproveAll, new AgentLimits { MaxDuration = TenMinutes }));

        _time.Advance(TimeSpan.FromHours(1));
        var reply = await session.SendAsync("Go.");

        Assert.False(reply.Stopped);
    }

    [Fact]
    public async Task TimeAtTheApprovalPromptDoesNotCountTowardMaxDuration()
    {
        var slowOperator = ApprovalPrompter.From((_, _) =>
        {
            _time.Advance(TimeSpan.FromHours(1));
            return true;
        });
        var backend = new ScriptedBackend().Turn(t => t.Shell("make deploy").Reply("done"));
        var runner = new AgentRunner(backend, slowOperator, _time);

        var result = await runner.RunAsync(Options(ToolPolicy.AskForEverything, new AgentLimits { MaxDuration = TenMinutes }), "Ship it.");

        Assert.False(result.Reply.Stopped);
        Assert.True(Assert.Single(backend.Decisions).Allowed);
    }

    [Fact]
    public async Task AgentLimitsNoneNeverStopsTheSession()
    {
        var backend = new ScriptedBackend().Turn(t =>
        {
            for (var i = 0; i < 100; i++)
            {
                t.Shell("ls").Shell("curl x");
            }

            t.Elapse(_time, TimeSpan.FromDays(1)).Reply("done");
        });
        var runner = new AgentRunner(backend, time: _time);

        var result = await runner.RunAsync(Options(new WorkspacePolicy(_workspace.Path), AgentLimits.None), "Go.");

        Assert.False(result.Reply.Stopped);
        Assert.Equal(100, result.Stats.ToolCalls);
        Assert.Equal(100, result.Stats.Refusals);
    }

    [Fact]
    public async Task AStopRuleStopsTheSession()
    {
        var noDeploys = AgentObserver.StopWhen(e => e is ToolCallStarted { Detail: "make deploy" } ? "deploy attempted" : null);
        var backend = new ScriptedBackend().Turn(t => t.Shell("make deploy").Shell("ls").Reply("done"));
        var runner = new AgentRunner(backend);

        var result = await runner.RunAsync(Options(ToolPolicy.ApproveAll, stopRules: [noDeploys]), "Go.");

        Assert.Equal("deploy attempted", result.Reply.StopReason);
        Assert.Single(backend.Decisions);
    }

    [Fact]
    public async Task WhenSeveralRulesFireTheFirstReasonWins()
    {
        var first = AgentObserver.StopWhen(e => e is ToolCallStarted ? "first rule" : null);
        var second = AgentObserver.StopWhen(e => e is ToolCallStarted ? "second rule" : null);
        var backend = new ScriptedBackend().Turn(t => t.Shell("ls").Reply("done"));
        var runner = new AgentRunner(backend);

        var result = await runner.RunAsync(Options(ToolPolicy.ApproveAll, [first, second]), "Go.");

        Assert.Equal("first rule", result.Reply.StopReason);
        Assert.Equal("first rule", Assert.Single(_events.OfType<SessionStopped>()).Reason);
    }

    [Fact]
    public async Task ALaterStopDoesNotReplaceTheFirstReason()
    {
        var runner = new AgentRunner(new ScriptedBackend());
        await using var session = await runner.StartAsync(Options(ToolPolicy.ApproveAll, AgentLimits.None));
        session.Stop("stopped by the app");

        session.Stop("a second reason");

        Assert.Equal("stopped by the app", session.StopReason);
    }

    [Fact]
    public async Task AStopIsReportedToObservers()
    {
        var backend = new ScriptedBackend().Turn(t => t.Shell("ls").Shell("ls").Reply("done"));
        var runner = new AgentRunner(backend);

        await runner.RunAsync(Options(ToolPolicy.ApproveAll, new AgentLimits { MaxToolCalls = 1 }), "Go.");

        Assert.Equal("agent stopped: more than 1 tool calls", Assert.Single(_events.OfType<SessionStopped>()).Reason);
    }

    [Fact]
    public async Task AfterAStopTheNextTurnReturnsStoppedWithoutReachingTheModel()
    {
        var backend = new ScriptedBackend()
            .Turn(t => t.Shell("ls").Shell("ls").Reply("first"))
            .Reply("second");
        var runner = new AgentRunner(backend);
        await using var session = await runner.StartAsync(Options(ToolPolicy.ApproveAll, new AgentLimits { MaxToolCalls = 1 }));
        await session.SendAsync("One.");

        var reply = await session.SendAsync("Two.");

        Assert.Equal(new AgentReply(null, "agent stopped: more than 1 tool calls"), reply);
        Assert.Equal(["One."], backend.Messages);
        Assert.Equal(["One."], _events.OfType<UserMessage>().Select(m => m.Text));
    }

    [Fact]
    public async Task StopEndsTheSessionForAReasonOfYourOwn()
    {
        var backend = new ScriptedBackend().Reply("never sent");
        var runner = new AgentRunner(backend);
        await using var session = await runner.StartAsync(Options(ToolPolicy.ApproveAll, AgentLimits.None));

        session.Stop("the work item was closed");
        var reply = await session.SendAsync("Go.");

        Assert.Equal("the work item was closed", reply.StopReason);
        Assert.Empty(backend.Messages);
    }

    [Fact]
    public async Task CancellingATurnThrowsAndDoesNotStopTheSession()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var backend = new ScriptedBackend().Reply("first").Reply("second");
        var runner = new AgentRunner(backend);
        await using var session = await runner.StartAsync(Options(ToolPolicy.ApproveAll, AgentLimits.None));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.SendAsync("One.", cancellation.Token));

        Assert.Null(session.StopReason);
    }

    [Fact]
    public async Task ABackendsOwnCancellationIsNotReportedAsTheTimeBudget()
    {
        var backend = new ScriptedBackend().Turn(t => t.Step((_, _) => throw new TaskCanceledException("HTTP request timed out")));
        var runner = new AgentRunner(backend);
        await using var session = await runner.StartAsync(Options(ToolPolicy.ApproveAll, AgentLimits.None));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.SendAsync("Go."));
    }

    private AgentSessionOptions Options(IToolPolicy policy, AgentLimits limits, IReadOnlyList<IStopRule>? stopRules = null) => new()
    {
        WorkingDirectory = _workspace.Path,
        Policy = policy,
        Limits = limits,
        Observers = [_events],
        StopRules = stopRules ?? [],
    };

    private AgentSessionOptions Options(IToolPolicy policy, IReadOnlyList<IStopRule> stopRules) => Options(policy, AgentLimits.None, stopRules);
}
