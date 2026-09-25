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
        var monitor = new ProgressMonitor(5, 3, initialErrors: 20);

        Assert.Null(monitor.RecordBuild(Failed12));
        Assert.Null(monitor.RecordBuild(Failed9));
        Assert.Null(monitor.RecordBuild(Failed12));
        Assert.Null(monitor.RecordBuild(Failed12));
    }

    [Fact]
    public void StopsAfterThreeBuildsThatDontBeatTheLowestCount()
    {
        var monitor = new ProgressMonitor(5, 3, initialErrors: 12);

        Assert.Null(monitor.RecordBuild(Failed12));
        Assert.Null(monitor.RecordBuild(Failed12));
        var reason = monitor.RecordBuild(Failed12);

        Assert.Contains("3 builds without reducing the errors below 12", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AGreenBuildResetsTheCount()
    {
        var monitor = new ProgressMonitor(5, 3, initialErrors: 9);

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
        var monitor = new ProgressMonitor(5, 1, initialErrors: 9);

        Assert.Null(monitor.RecordBuild("  src/A.cs(3,5): error CS0117: truncated by head"));
    }

    [Fact]
    public void StopsOnceRefusalsExceedTheLimit()
    {
        var monitor = new ProgressMonitor(maxRefusals: 2, 3, 0);

        Assert.Null(monitor.RecordRefusal());
        Assert.Null(monitor.RecordRefusal());
        Assert.Contains("more than 2 refused actions", monitor.RecordRefusal(), StringComparison.Ordinal);
    }

    [Fact]
    public void ZeroDisablesBothChecks()
    {
        var monitor = new ProgressMonitor(0, 0, initialErrors: 1);

        for (var i = 0; i < 10; i++)
        {
            Assert.Null(monitor.RecordRefusal());
            Assert.Null(monitor.RecordBuild(Failed12));
        }
    }
}
