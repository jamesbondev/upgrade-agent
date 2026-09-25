using AgentHarness.Policies;
using AgentHarness.Testing;
using AgentHarness.Tests.TestSupport;

namespace AgentHarness.Tests;

public sealed class AgentRunnerTests : IDisposable
{
    private readonly TempDirectory _workspace = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task RunAsyncReturnsTheReplyAndTheStats()
    {
        var backend = new ScriptedBackend().Turn(t => t.Shell("ls").Usage(100, 20).Reply("done"));
        var runner = new AgentRunner(backend);

        var result = await runner.RunAsync(Options(ToolPolicy.ApproveAll), "Look around.");

        Assert.Equal(new AgentReply("done", null), result.Reply);
        Assert.Equal(1, result.Stats.ToolCalls);
        Assert.Equal(1, result.Stats.ModelCalls);
    }

    [Fact]
    public async Task StartAsyncPassesTheSessionOptionsToTheBackend()
    {
        var tool = AgentTool.Create(() => "ok", "get_status");
        var backend = new ScriptedBackend();
        var runner = new AgentRunner(backend);

        await using var session = await runner.StartAsync(new AgentSessionOptions
        {
            WorkingDirectory = _workspace.Path,
            Instructions = "You fix builds.",
            Policy = ToolPolicy.ApproveAll,
            Tools = [tool],
            UseBuiltInTools = false,
            AllowWebFetch = true,
        });

        var settings = backend.LastSettings!;
        Assert.Equal(_workspace.Path, settings.WorkingDirectory);
        Assert.Equal("You fix builds.", settings.Instructions);
        Assert.Same(tool, Assert.Single(settings.Tools));
        Assert.False(settings.UseBuiltInTools);
        Assert.True(settings.AllowWebFetch);
    }

    [Fact]
    public async Task StartAsyncRequiresAWorkingDirectory()
    {
        var runner = new AgentRunner(new ScriptedBackend());

        await Assert.ThrowsAsync<ArgumentException>(() => runner.StartAsync(new AgentSessionOptions { WorkingDirectory = " ", Policy = ToolPolicy.ApproveAll }));
    }

    [Fact]
    public async Task RunAsyncWithAQuestionAsksForAStructuredReplyAfterTheTurn()
    {
        var backend = new ScriptedBackend()
            .Reply("done")
            .Turn(t => t.ReplyJson(new Outcome { Fixed = true }));
        var runner = new AgentRunner(backend);

        var result = await runner.RunAsync<Outcome>(Options(ToolPolicy.ApproveAll), "Fix the build.", "Did you fix it?");

        Assert.Equal("done", result.Reply.Text);
        Assert.True(result.Structured?.Value?.Fixed);
    }

    [Fact]
    public async Task RunAsyncWithAQuestionDoesNotAskWhenTheTurnWasStopped()
    {
        var backend = new ScriptedBackend()
            .Turn(t => t.Shell("a").Shell("b").Reply("done"))
            .Turn(t => t.ReplyJson(new Outcome { Fixed = true }));
        var runner = new AgentRunner(backend);

        var result = await runner.RunAsync<Outcome>(Options(ToolPolicy.ApproveAll, new AgentLimits { MaxToolCalls = 1 }), "Fix the build.", "Did you fix it?");

        Assert.True(result.Reply.Stopped);
        Assert.Null(result.Structured);
        Assert.Equal(["Fix the build."], backend.Messages);
    }

    [Fact]
    public async Task OneRunnerServesManySessions()
    {
        var backend = new ScriptedBackend().Reply("first").Reply("second");
        var runner = new AgentRunner(backend);

        var first = await runner.RunAsync(Options(ToolPolicy.ApproveAll), "One.");
        var second = await runner.RunAsync(Options(ToolPolicy.ApproveAll), "Two.");

        Assert.Equal("first", first.Reply.Text);
        Assert.Equal("second", second.Reply.Text);
    }

    private AgentSessionOptions Options(IToolPolicy policy, AgentLimits? limits = null) => new()
    {
        WorkingDirectory = _workspace.Path,
        Policy = policy,
        Limits = limits ?? AgentLimits.None,
    };

    public sealed class Outcome
    {
        public bool Fixed { get; init; }
    }
}
