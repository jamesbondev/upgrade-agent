namespace AgentHarness.Tests;

public class AgentEventsTests
{
    [Theory]
    [InlineData("claude-sonnet-4.5", "claude-sonnet-4.5", false)]
    [InlineData("claude-sonnet-4.5-20250929", "claude-sonnet-4.5", false)]
    [InlineData("gpt-4.1", "claude-sonnet-4.5", true)]
    [InlineData("gpt-4.1", null, false)]
    public void ModelServedIsAFallbackWhenTheServedModelIsNotTheOneAskedFor(string served, string? requested, bool expected)
    {
        Assert.Equal(expected, new ModelServed(served, requested).IsFallback);
    }
}
