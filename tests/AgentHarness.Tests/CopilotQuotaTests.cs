using AgentHarness.Copilot;

namespace AgentHarness.Tests;

public class CopilotQuotaTests
{
    [Theory]
    [InlineData("You have exceeded your monthly quota of premium requests", true)]
    [InlineData("Error: Quota exceeded for model", true)]
    [InlineData("rate limited", false)]
    [InlineData(null, false)]
    public void IsExceeded_RecognisesQuotaMessages(string? message, bool expected) =>
        Assert.Equal(expected, CopilotQuota.IsExceeded(message));
}
