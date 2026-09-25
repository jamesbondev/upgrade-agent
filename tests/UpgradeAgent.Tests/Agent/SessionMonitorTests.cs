using AgentHarness;
using UpgradeAgent.Agent;
using UpgradeAgent.Agent.Activities;

namespace UpgradeAgent.Tests.Agent;

public class SessionMonitorTests
{
    private static readonly string Worktree = Path.Combine(Path.GetTempPath(), "ua-monitor", "wt");

    private readonly List<ActivityEvent> _written = [];
    private readonly RequiredReading _reading = new(["/pkg/MIGRATION.md"]);
    private readonly SessionMonitor _monitor;

    public SessionMonitorTests() => _monitor = new SessionMonitor(Worktree, new ListSink(_written), _reading);

    [Fact]
    public void ToolCallsAreShownRelativeToTheWorktreeAndMarkNotesAsRead()
    {
        _monitor.OnEvent(new ToolCallStarted("1", ToolKind.Read, "view", Path.Combine(Worktree, "src", "A.cs"), """{"path":"/pkg/MIGRATION.md"}"""));

        Assert.Equal(new ToolStarted(ToolKind.Read, "view", Path.Combine("src", "A.cs")), Assert.Single(_written));
        Assert.Empty(_reading.Unread);
    }

    [Fact]
    public void BuildAndTestOutputIsRead()
    {
        _monitor.OnEvent(Completed("dotnet build x.slnx --no-restore", "src/A.cs(3,5): error CS0117: gone\n\nBuild FAILED.\n    0 Warning(s)\n    2 Error(s)\n"));
        _monitor.OnEvent(Completed("dotnet test x.slnx --no-build", "Passed!  - Failed:     1, Passed:    41, Skipped:     0, Total:    42"));
        _monitor.OnEvent(Completed("cat src/A.cs", "Build FAILED."));

        Assert.Equal(2, _written.Count);
        Assert.Equal(2, Assert.IsType<BuildChecked>(_written[0]).Errors);
        Assert.Equal(new TestsChecked(41, 1), _written[1]);
    }

    [Fact]
    public void FailedToolsAreShownAndUnpairedCompletionsIgnored()
    {
        _monitor.OnEvent(new ToolCallCompleted("1", false, null, "no such file") { Call = new ToolCallStarted("1", ToolKind.Read, "view", "x") });
        _monitor.OnEvent(new ToolCallCompleted("2", false, null, "orphan"));

        Assert.Equal(new ToolFailed("no such file"), Assert.Single(_written));
    }

    [Fact]
    public void MessagesShowTheirFirstLineAndKeepTheTranscriptExceptDuringTheSummary()
    {
        _monitor.OnEvent(new AssistantMessage("\n  Fixing the call sites.\nDetails follow."));
        _monitor.SummaryMode = true;
        _monitor.OnEvent(new AssistantMessage("""{"packages":[]}"""));

        Assert.Equal([new AgentMessage("Fixing the call sites."), new Transcript("AGENT", "\n  Fixing the call sites.\nDetails follow.")], _written);
    }

    [Fact]
    public void ServedModelsRefusalsAndStopsBecomeNotes()
    {
        _monitor.OnEvent(new ModelServed("claude-sonnet-4.5-20250929", "claude-sonnet-4.5"));
        _monitor.OnEvent(new ModelServed("gpt-5-mini", "claude-sonnet-4.5"));
        _monitor.OnEvent(new ToolRefused(new ShellRequest("git commit"), "git commit", "read-only git only"));
        _monitor.OnEvent(new SessionStopped("agent stopped: more than 5 refused actions"));

        Assert.Equal(
            [
                new Note("model: claude-sonnet-4.5-20250929"),
                new Note("warning: requested model claude-sonnet-4.5 but the provider is serving gpt-5-mini (not available on this plan?)"),
                new ActionRefused("git commit", "read-only git only"),
                new Note("agent stopped: more than 5 refused actions"),
            ],
            _written);
    }

    private static ToolCallCompleted Completed(string command, string output) =>
        new("c", true, output, null) { Call = new ToolCallStarted("c", ToolKind.Shell, "bash", command) };

    private sealed class ListSink(List<ActivityEvent> events) : IActivitySink
    {
        public void Write(ActivityEvent activity) => events.Add(activity);
    }
}
