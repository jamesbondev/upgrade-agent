using AgentHarness.Policies;
using AgentHarness.Testing;
using AgentHarness.Tests.TestSupport;
using Microsoft.Extensions.Time.Testing;

namespace AgentHarness.Tests;

/// <summary><see cref="AgentSession.AskAsync{T}(string, CancellationToken)"/>: a structured reply, in a turn where every tool is refused.</summary>
public sealed class AgentSessionStructuredReplyTests : IDisposable
{
    private readonly TempDirectory _workspace = new();
    private readonly EventRecorder _events = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task AskAsyncParsesTheReplyIntoTheType()
    {
        var backend = new ScriptedBackend().Turn(t => t.ReplyJson(new Summary { Title = "Fixed the build", Files = ["src/A.cs"] }));
        await using var session = await new AgentRunner(backend).StartAsync(Options(ToolPolicy.ApproveAll));

        var reply = await session.AskAsync<Summary>("Summarise your changes.");

        Assert.Null(reply.Error);
        Assert.Equal("Fixed the build", reply.Value?.Title);
        Assert.Equal(["src/A.cs"], reply.Value?.Files);
    }

    [Fact]
    public async Task AskAsyncSendsTheQuestionWithTheSchema()
    {
        var backend = new ScriptedBackend().Turn(t => t.ReplyJson(new Summary { Title = "x" }));
        await using var session = await new AgentRunner(backend).StartAsync(Options(ToolPolicy.ApproveAll));

        await session.AskAsync<Summary>("Summarise your changes.");

        Assert.Equal(StructuredOutput.PromptFor<Summary>("Summarise your changes."), Assert.Single(backend.Messages));
    }

    [Fact]
    public async Task AskAsyncReturnsAnErrorInsteadOfThrowingForAReplyThatIsNotJson()
    {
        var backend = new ScriptedBackend().Reply("Sorry, I couldn't finish.");
        await using var session = await new AgentRunner(backend).StartAsync(Options(ToolPolicy.ApproveAll));

        var reply = await session.AskAsync<Summary>("Summarise your changes.");

        Assert.Null(reply.Value);
        Assert.NotNull(reply.Error);
        Assert.Equal("Sorry, I couldn't finish.", reply.Text);
    }

    [Fact]
    public async Task AskAsyncReturnsAnErrorForAMissingRequiredProperty()
    {
        var backend = new ScriptedBackend().Reply("""{"files":["src/A.cs"]}""");
        await using var session = await new AgentRunner(backend).StartAsync(Options(ToolPolicy.ApproveAll));

        var reply = await session.AskAsync<Summary>("Summarise your changes.");

        Assert.Null(reply.Value);
        Assert.NotNull(reply.Error);
    }

    [Fact]
    public async Task AskAsyncAcceptsJsonInAMarkdownFence()
    {
        var backend = new ScriptedBackend().Reply("""
            ```json
            {"title": "Fixed the build"}
            ```
            """);
        await using var session = await new AgentRunner(backend).StartAsync(Options(ToolPolicy.ApproveAll));

        var reply = await session.AskAsync<Summary>("Summarise your changes.");

        Assert.Equal("Fixed the build", reply.Value?.Title);
    }

    [Fact]
    public async Task AskAsyncReturnsAnErrorWhenTheBackendFails()
    {
        var backend = new ScriptedBackend();
        await using var session = await new AgentRunner(backend).StartAsync(Options(ToolPolicy.ApproveAll));

        var reply = await session.AskAsync<Summary>("Summarise your changes.");

        Assert.Null(reply.Value);
        Assert.Contains("no turn left", reply.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AskAsyncReturnsAnErrorWhenTheReplyTakesLongerThanItsTimeout()
    {
        var time = new FakeTimeProvider();
        var backend = new ScriptedBackend().Turn(t => t.Elapse(time, TimeSpan.FromMinutes(5)).ReplyJson(new Summary { Title = "late" }));
        await using var session = await new AgentRunner(backend, time: time).StartAsync(Options(ToolPolicy.ApproveAll));

        var reply = await session.AskAsync<Summary>("Summarise your changes.", timeout: TimeSpan.FromMinutes(1));

        Assert.Equal("the agent didn't reply in time", reply.Error);
    }

    [Fact]
    public async Task EveryToolIsRefusedDuringAskAsync()
    {
        var deploy = AgentTool.Create(() => "deployed", "deploy", requiresApproval: true);
        var backend = new ScriptedBackend().Turn(t => t
            .Shell("ls")
            .Read("README.md")
            .Edit("src/A.cs", "class A;")
            .Fetch("https://example.com")
            .CallTool("deploy")
            .ReplyJson(new Summary { Title = "x" }));
        await using var session = await new AgentRunner(backend).StartAsync(Options(ToolPolicy.ApproveAll, tools: [deploy], allowWebFetch: true));

        await session.AskAsync<Summary>("Summarise your changes.");

        Assert.Equal(5, backend.Decisions.Count);
        Assert.All(backend.Decisions, d => Assert.False(d.Allowed));
        Assert.All(backend.Decisions, d => Assert.Equal("No tools now: reply with the answer only.", d.Feedback));
        Assert.False(_workspace.Exists("src/A.cs"));
    }

    [Fact]
    public async Task EvenToolsThatNeedNoApprovalAreRefusedDuringAskAsync()
    {
        var calls = 0;
        var lookup = AgentTool.Create(() => ++calls, "get_ticket");
        var backend = new ScriptedBackend().Turn(t => t.CallTool("get_ticket").ReplyJson(new Summary { Title = "x" }));
        await using var session = await new AgentRunner(backend).StartAsync(Options(ToolPolicy.ApproveAll, tools: [lookup]));

        await session.AskAsync<Summary>("Summarise your changes.");

        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task RefusalsDuringAskAsyncDoNotCountTowardTheRefusalLimit()
    {
        var backend = new ScriptedBackend()
            .Turn(t => t.Shell("a").Shell("b").Shell("c").ReplyJson(new Summary { Title = "x" }))
            .Reply("still going");
        await using var session = await new AgentRunner(backend).StartAsync(Options(ToolPolicy.ApproveAll, new AgentLimits { MaxRefusals = 1 }));

        await session.AskAsync<Summary>("Summarise your changes.");
        var next = await session.SendAsync("Carry on.");

        Assert.Null(session.StopReason);
        Assert.Equal("still going", next.Text);
    }

    [Fact]
    public async Task NoStopRuleFiresDuringAskAsync()
    {
        var alwaysStop = AgentObserver.StopWhen(_ => "a stop rule fired");
        var backend = new ScriptedBackend().Turn(t => t.Shell("ls").ReplyJson(new Summary { Title = "x" }));
        await using var session = await new AgentRunner(backend).StartAsync(Options(ToolPolicy.ApproveAll, stopRules: [alwaysStop]));

        var reply = await session.AskAsync<Summary>("Summarise your changes.");

        Assert.Equal("x", reply.Value?.Title);
        Assert.Null(session.StopReason);
    }

    [Fact]
    public async Task StopDuringAskAsyncIsNotLost()
    {
        AgentSession? session = null;
        var backend = new ScriptedBackend().Turn(t => t
            .Step((_, _) =>
            {
                session!.Stop("the work item was closed");
                return Task.CompletedTask;
            })
            .ReplyJson(new Summary { Title = "x" }));
        await using var started = session = await new AgentRunner(backend).StartAsync(Options(ToolPolicy.ApproveAll));

        await session.AskAsync<Summary>("Summarise your changes.");

        Assert.Equal("the work item was closed", session.StopReason);
    }

    [Fact]
    public async Task AskAsyncWorksAfterANormalTurn()
    {
        var backend = new ScriptedBackend()
            .Turn(t => t.Shell("ls").Reply("done"))
            .Turn(t => t.ReplyJson(new Summary { Title = "Looked around" }));
        await using var session = await new AgentRunner(backend).StartAsync(Options(ToolPolicy.ApproveAll));

        await session.SendAsync("Look around.");
        var reply = await session.AskAsync<Summary>("Summarise.");

        Assert.Equal("Looked around", reply.Value?.Title);
    }

    private AgentSessionOptions Options(
        IToolPolicy policy, AgentLimits? limits = null, IReadOnlyList<AgentTool>? tools = null, bool allowWebFetch = false, IReadOnlyList<IStopRule>? stopRules = null) => new()
    {
        WorkingDirectory = _workspace.Path,
        Policy = policy,
        Limits = limits ?? AgentLimits.None,
        Tools = tools ?? [],
        AllowWebFetch = allowWebFetch,
        Observers = [_events],
        StopRules = stopRules ?? [],
    };

    public sealed class Summary
    {
        public required string Title { get; init; }

        public IReadOnlyList<string> Files { get; init; } = [];
    }
}
