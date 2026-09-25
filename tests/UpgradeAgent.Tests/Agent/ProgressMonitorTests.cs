using AgentHarness;
using UpgradeAgent.Agent;

namespace UpgradeAgent.Tests.Agent;

public class ProgressMonitorTests
{
    private const string Failed12 = "Build FAILED.\n\n    0 Warning(s)\n    12 Error(s)\n";
    private const string Failed9 = "Build FAILED.\n\n    0 Warning(s)\n    9 Error(s)\n";
    private const string Succeeded = "Build succeeded.\n    0 Warning(s)\n    0 Error(s)\n";

    [Fact]
    public void KeepsGoingWhileErrorsDrop()
    {
        var monitor = new ProgressMonitor(3, initialErrors: 20);

        Assert.Null(monitor.RecordBuild(Failed12));
        Assert.Null(monitor.RecordBuild(Failed9));
        Assert.Null(monitor.RecordBuild(Failed12));
        Assert.Null(monitor.RecordBuild(Failed12));
    }

    [Fact]
    public void StopsAfterThreeBuildsThatDontBeatTheLowestCount()
    {
        var monitor = new ProgressMonitor(3, initialErrors: 12);

        Assert.Null(monitor.RecordBuild(Failed12));
        Assert.Null(monitor.RecordBuild(Failed12));
        var reason = monitor.RecordBuild(Failed12);

        Assert.Equal("agent stopped: 3 builds without reducing the errors below 12", reason);
    }

    [Fact]
    public void AGreenBuildResetsTheCount()
    {
        var monitor = new ProgressMonitor(3, initialErrors: 9);

        Assert.Null(monitor.RecordBuild(Failed12));
        Assert.Null(monitor.RecordBuild(Failed12));
        Assert.Null(monitor.RecordBuild(Succeeded));
        Assert.Null(monitor.RecordBuild(Failed12));
        Assert.Null(monitor.RecordBuild(Failed12));
        Assert.Null(monitor.RecordBuild(Failed12));
        Assert.NotNull(monitor.RecordBuild(Failed12));
    }

    [Fact]
    public void UnrecognisedOutputIsIgnored()
    {
        var monitor = new ProgressMonitor(1, initialErrors: 9);

        Assert.Null(monitor.RecordBuild("  src/A.cs(3,5): error CS0117: truncated by head"));
    }

    [Fact]
    public void ZeroDisablesTheCheck()
    {
        var monitor = new ProgressMonitor(0, initialErrors: 1);

        for (var i = 0; i < 10; i++)
        {
            Assert.Null(monitor.RecordBuild(Failed12));
        }
    }

    [Fact]
    public void AsAStopRuleItReadsOnlySuccessfulDotnetBuilds()
    {
        var monitor = new ProgressMonitor(1, initialErrors: 9);

        Assert.Null(monitor.Check(Completed(ToolKind.Shell, "dotnet test x.slnx --no-build", Failed12)));
        Assert.Null(monitor.Check(Completed(ToolKind.Read, "dotnet build notes.md", Failed12)));
        Assert.Null(monitor.Check(Completed(ToolKind.Shell, "dotnet build x.slnx --no-restore", Failed12) with { Success = false }));
        Assert.Null(monitor.Check(new ToolCallCompleted("unpaired", true, Failed12, null)));
        Assert.Equal(
            "agent stopped: 1 builds without reducing the errors below 9",
            monitor.Check(Completed(ToolKind.Shell, "dotnet build x.slnx --no-restore 2>&1 | tail -5", Failed12)));
    }

    private static ToolCallCompleted Completed(ToolKind kind, string detail, string output) =>
        new("call-1", true, output, null) { Call = new ToolCallStarted("call-1", kind, "bash", detail) };
}
