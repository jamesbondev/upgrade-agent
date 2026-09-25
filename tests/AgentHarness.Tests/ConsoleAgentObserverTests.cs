namespace AgentHarness.Tests;

public sealed class ConsoleAgentObserverTests : IDisposable
{
    private static readonly string Workspace = Path.Combine(Path.GetTempPath(), "agent-harness-console", "ws");

    private readonly StringWriter _output = new() { NewLine = "\n" };

    private string[] Lines => _output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);

    public void Dispose() => _output.Dispose();

    [Fact]
    public void EditsAreShownRelativeToTheWorkingDirectory()
    {
        var observer = new ConsoleAgentObserver(Workspace, _output);

        observer.OnEvent(new ToolCallStarted("1", ToolKind.Edit, "edit", Path.Combine(Workspace, "src", "a.cs")));

        Assert.Equal([$"    ✎ {Path.Combine("src", "a.cs")}"], Lines);
    }

    [Fact]
    public void ShellCommandsAreShownRelativeToTheWorkingDirectory()
    {
        var observer = new ConsoleAgentObserver(Workspace, _output);

        observer.OnEvent(new ToolCallStarted("1", ToolKind.Shell, "bash", $"cd {Workspace} && ls"));

        Assert.Equal(["    $ cd . && ls"], Lines);
    }

    [Fact]
    public void WithoutAWorkingDirectoryPathsAreShownAsGiven()
    {
        var observer = new ConsoleAgentObserver(output: _output);
        var path = Path.Combine(Workspace, "a.cs");

        observer.OnEvent(new ToolCallStarted("1", ToolKind.Edit, "edit", path));

        Assert.Equal([$"    ✎ {path}"], Lines);
    }

    [Fact]
    public void OtherToolsShowTheToolName()
    {
        var observer = new ConsoleAgentObserver(Workspace, _output);

        observer.OnEvent(new ToolCallStarted("1", ToolKind.Read, "view", "README.md"));

        Assert.Equal(["    · view README.md"], Lines);
    }

    [Fact]
    public void RefusalsShowTheActionAndTheReason()
    {
        var observer = new ConsoleAgentObserver(Workspace, _output);

        observer.OnEvent(new ToolRefused(new ShellRequest("curl x"), "curl x", "No network access."));

        Assert.Equal(["    ⊘ refused curl x: No network access."], Lines);
    }

    [Fact]
    public void OperatorApprovalsAreShown()
    {
        var observer = new ConsoleAgentObserver(Workspace, _output);

        observer.OnEvent(new ToolApprovedByOperator(new FileWriteRequest("a.csproj"), "edit a.csproj"));

        Assert.Equal(["    ✓ approved edit a.csproj"], Lines);
    }

    [Fact]
    public void StopsShowTheReason()
    {
        var observer = new ConsoleAgentObserver(Workspace, _output);

        observer.OnEvent(new SessionStopped("agent stopped: more than 5 refused actions"));

        Assert.Equal(["  agent stopped: more than 5 refused actions"], Lines);
    }

    [Fact]
    public void FailedToolCallsShowTheError()
    {
        var observer = new ConsoleAgentObserver(Workspace, _output);

        observer.OnEvent(new ToolCallCompleted("1", false, null, "exit code 1"));

        Assert.Equal(["      ✗ exit code 1"], Lines);
    }

    [Fact]
    public void SuccessfulToolCallsPrintNothing()
    {
        var observer = new ConsoleAgentObserver(Workspace, _output);

        observer.OnEvent(new ToolCallCompleted("1", true, "lots of output", null));

        Assert.Empty(Lines);
    }

    [Fact]
    public void AssistantMessagesShowTheirFirstNonEmptyLine()
    {
        var observer = new ConsoleAgentObserver(Workspace, _output);

        observer.OnEvent(new AssistantMessage("\n  Fixed the build.\nDetails follow."));

        Assert.Equal(["  Fixed the build."], Lines);
    }

    [Fact]
    public void LongMessagesAreShortened()
    {
        var observer = new ConsoleAgentObserver(Workspace, _output);

        observer.OnEvent(new AssistantMessage(new string('x', 500)));

        var line = Assert.Single(Lines);
        Assert.EndsWith("…", line, StringComparison.Ordinal);
        Assert.True(line.Length < 150);
    }

    [Fact]
    public void AModelFallbackIsAWarning()
    {
        var observer = new ConsoleAgentObserver(Workspace, _output);

        observer.OnEvent(new ModelServed("gpt-4.1", "claude-sonnet-4.5"));

        Assert.Equal(["  warning: asked for claude-sonnet-4.5 but the provider is serving gpt-4.1"], Lines);
    }

    [Fact]
    public void TheServedModelIsShownWhenItIsTheOneAskedFor()
    {
        var observer = new ConsoleAgentObserver(Workspace, _output);

        observer.OnEvent(new ModelServed("claude-sonnet-4.5", "claude-sonnet-4.5"));

        Assert.Equal(["  model: claude-sonnet-4.5"], Lines);
    }
}
