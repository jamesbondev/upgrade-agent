using AgentHarness.Policies;
using AgentHarness.Testing;
using AgentHarness.Tests.TestSupport;

namespace AgentHarness.Tests.Testing;

/// <summary>The fake backend itself: what a script does, and how it fails.</summary>
public sealed class ScriptedBackendTests : IDisposable
{
    private readonly TempDirectory _workspace = new();
    private readonly EventRecorder _events = new();

    public void Dispose() => _workspace.Dispose();

    [Fact]
    public async Task RunningOutOfTurnsThrowsAClearError()
    {
        var backend = new ScriptedBackend().Reply("only one");
        await using var session = await new AgentRunner(backend).StartAsync(Options());
        await session.SendAsync("One.");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => session.SendAsync("Two."));

        Assert.Contains("no turn left", error.Message, StringComparison.Ordinal);
        Assert.Contains("Two.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MessagesRecordsEverythingTheAppSent()
    {
        var backend = new ScriptedBackend().Reply("a").Reply("b");
        await using var session = await new AgentRunner(backend).StartAsync(Options());

        await session.SendAsync("One.");
        await session.SendAsync("Two.");

        Assert.Equal(["One.", "Two."], backend.Messages);
    }

    [Fact]
    public async Task TheLastReplyIsTheTurnsReply()
    {
        var backend = new ScriptedBackend().Turn(t => t.Reply("draft").Shell("ls").Reply("final"));

        var result = await new AgentRunner(backend).RunAsync(Options(), "Go.");

        Assert.Equal("final", result.Reply.Text);
    }

    [Fact]
    public async Task ATurnWithoutAReplyReturnsNull()
    {
        var backend = new ScriptedBackend().Turn(t => t.Shell("ls"));

        var result = await new AgentRunner(backend).RunAsync(Options(), "Go.");

        Assert.Null(result.Reply.Text);
        Assert.False(result.Reply.Stopped);
    }

    [Fact]
    public async Task ShellCommandsAreNotRunButReturnTheirScriptedOutput()
    {
        var backend = new ScriptedBackend().Turn(t => t.Shell("dotnet build", output: "Build FAILED.", success: false).Reply("done"));

        await new AgentRunner(backend).RunAsync(Options(), "Build.");

        var completed = Assert.Single(_events.OfType<ToolCallCompleted>());
        Assert.False(completed.Success);
        Assert.Equal("Build FAILED.", completed.Error);
    }

    [Fact]
    public async Task ARedirectingShellCommandIsReportedAsWritingAFile()
    {
        var backend = new ScriptedBackend().Turn(t => t.Shell("ls", writesFile: true).Reply("done"));

        await new AgentRunner(backend).RunAsync(Options(), "Go.");

        Assert.Equal(new ShellRequest("ls", WritesFile: true), Assert.Single(backend.Decisions).Request);
    }

    [Fact]
    public async Task AReadReturnsTheRealFilesContent()
    {
        _workspace.Write("README.md", "# Hello");
        var backend = new ScriptedBackend().Turn(t => t.Read("README.md").Reply("done"));

        await new AgentRunner(backend).RunAsync(Options(), "Read it.");

        Assert.Equal("# Hello", Assert.Single(_events.OfType<ToolCallCompleted>()).Output);
    }

    [Fact]
    public async Task AReadOfAMissingFileFails()
    {
        var backend = new ScriptedBackend().Turn(t => t.Read("missing.md").Reply("done"));

        await new AgentRunner(backend).RunAsync(Options(), "Read it.");

        Assert.False(Assert.Single(_events.OfType<ToolCallCompleted>()).Success);
    }

    [Fact]
    public async Task CallingAToolTheSessionDoesNotHaveFailsTheCall()
    {
        var backend = new ScriptedBackend().Turn(t => t.CallTool("no_such_tool").Reply("done"));

        await new AgentRunner(backend).RunAsync(Options(), "Go.");

        var completed = Assert.Single(_events.OfType<ToolCallCompleted>());
        Assert.False(completed.Success);
        Assert.Contains("does not exist", completed.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACustomToolGetsTheScriptedArguments()
    {
        var tool = AgentTool.Create((string id, int count) => $"{id}x{count}", "repeat");
        var backend = new ScriptedBackend().Turn(t => t.CallTool("repeat", new { id = "ab", count = 3 }).Reply("done"));

        await new AgentRunner(backend).RunAsync(Options(tools: [tool]), "Go.");

        Assert.Equal("abx3", Assert.Single(_events.OfType<ToolCallCompleted>()).Output);
    }

    private AgentSessionOptions Options(IReadOnlyList<AgentTool>? tools = null) => new()
    {
        WorkingDirectory = _workspace.Path,
        Policy = ToolPolicy.ApproveAll,
        Limits = AgentLimits.None,
        Tools = tools ?? [],
        Observers = [_events],
    };
}
