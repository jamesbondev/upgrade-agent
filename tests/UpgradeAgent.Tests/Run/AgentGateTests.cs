using UpgradeAgent.Run;
using UpgradeAgent.Tests.TestSupport;

namespace UpgradeAgent.Tests.Run;

public class AgentGateTests
{
    [Theory]
    [InlineData(51, 50, true)]
    [InlineData(50, 50, false)]
    [InlineData(500, 0, false)]
    [InlineData(0, 50, false)]
    public void RejectsBumpsThatBreakTooMuchForTheAgent(int errors, int limit, bool rejected)
    {
        var reason = AgentGate.TooLargeForAgent(TestData.Build(errors), limit);

        Assert.Equal(rejected, reason is not null);
        if (rejected)
        {
            Assert.Contains($"{errors} build errors after the bump (limit {limit})", reason, StringComparison.Ordinal);
        }
    }
}
