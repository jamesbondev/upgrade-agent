using UpgradeAgent.Build;
using UpgradeAgent.Run;

namespace UpgradeAgent.Tests.Run;

public class AgentGateTests
{
    [Theory]
    [InlineData(false, 51, 50, true)]
    [InlineData(false, 50, 50, false)]
    [InlineData(false, 500, 0, false)]
    [InlineData(true, 0, 50, false)]
    public void RejectsBumpsThatBreakTooMuchForTheAgent(bool succeeded, int errors, int limit, bool rejected)
    {
        var build = new BuildResult(
            succeeded,
            Enumerable.Range(0, errors).Select(i => new Diagnostic("error", "CS0117", $"e{i}", "a.cs", i)).ToList(),
            [], "", TimeSpan.Zero);

        var reason = RunOrchestrator.TooLargeForAgent(build, limit);

        Assert.Equal(rejected, reason is not null);
        if (rejected)
        {
            Assert.Contains($"{errors} build errors after the bump (limit {limit})", reason, StringComparison.Ordinal);
        }
    }
}
