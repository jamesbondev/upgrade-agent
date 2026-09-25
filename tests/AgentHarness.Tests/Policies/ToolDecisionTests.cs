using AgentHarness.Policies;

namespace AgentHarness.Tests.Policies;

public class ToolDecisionTests
{
    [Fact]
    public void ApproveHasADefaultReason()
    {
        var decision = ToolDecision.Approve();

        Assert.Equal(ToolVerdict.Approve, decision.Verdict);
        Assert.Equal("approved", decision.Reason);
    }

    [Fact]
    public void AskCarriesThePromptAndTheFeedbackForADecline()
    {
        var decision = ToolDecision.Ask("deploys to production", prompt: "Deploy main to prod?", declinedFeedback: "Stop after the build.");

        Assert.Equal(ToolVerdict.Ask, decision.Verdict);
        Assert.Equal("deploys to production", decision.Reason);
        Assert.Equal("Deploy main to prod?", decision.Prompt);
        Assert.Equal("Stop after the build.", decision.DeclinedFeedback);
    }

    [Fact]
    public void AskWithoutPromptOrFeedbackLeavesThemForTheSessionToFillIn()
    {
        var decision = ToolDecision.Ask("edit to a.csproj");

        Assert.Null(decision.Prompt);
        Assert.Null(decision.DeclinedFeedback);
    }

    [Fact]
    public void RejectCountsTowardTheRefusalLimitByDefault()
    {
        var decision = ToolDecision.Reject("Use the edit tool.");

        Assert.Equal(ToolVerdict.Reject, decision.Verdict);
        Assert.Equal("Use the edit tool.", decision.Reason);
        Assert.True(decision.CountsTowardRefusalLimit);
        Assert.Null(decision.LogReason);
    }

    [Fact]
    public void ANudgeIsARejectThatDoesNotCountTowardTheRefusalLimit()
    {
        var nudge = ToolDecision.Reject("Read the migration guide first.") with { CountsTowardRefusalLimit = false, LogReason = "nudge: read docs" };

        Assert.False(nudge.CountsTowardRefusalLimit);
        Assert.Equal("nudge: read docs", nudge.LogReason);
    }
}
