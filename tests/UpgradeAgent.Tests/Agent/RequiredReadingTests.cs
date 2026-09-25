using AgentHarness;
using AgentHarness.Policies;
using UpgradeAgent.Agent;

namespace UpgradeAgent.Tests.Agent;

public class RequiredReadingTests
{
    private const string Notes = "/pkg/big.lib/3.0.0/MIGRATION.md";

    private static readonly IToolPolicy Allow = ToolPolicy.ApproveAll;

    [Fact]
    public async Task EditsAreRefusedAsANudgeUntilTheNotesAreRead()
    {
        var reading = new RequiredReading([Notes]);

        var decision = await reading.Guard(Allow).EvaluateAsync(new FileWriteRequest("/wt/src/A.cs"), CancellationToken.None);

        Assert.Equal(ToolVerdict.Reject, decision.Verdict);
        Assert.Equal($"Read the migration notes before editing: {Notes}. They name the replacement APIs.", decision.Reason);
        Assert.False(decision.CountsTowardRefusalLimit);
        Assert.Equal("migration notes not read yet", decision.LogReason);
    }

    [Fact]
    public async Task ReadingTheNotesLetsTheInnerDecisionThrough()
    {
        var reading = new RequiredReading([Notes]);

        reading.MarkRead($$"""{"path":"{{Notes}}"}""");

        Assert.Empty(reading.Unread);
        Assert.Equal(ToolVerdict.Approve, (await reading.Guard(Allow).EvaluateAsync(new FileWriteRequest("/wt/src/A.cs"), CancellationToken.None)).Verdict);
    }

    [Fact]
    public async Task EditsTheOperatorWouldBeAskedAboutAreRefusedToo()
    {
        var guarded = new RequiredReading([Notes]).Guard(ToolPolicy.AskForEverything);

        Assert.Equal(ToolVerdict.Reject, (await guarded.EvaluateAsync(new FileWriteRequest("/wt/App.csproj"), CancellationToken.None)).Verdict);
    }

    [Fact]
    public async Task TheInnerRefusalWinsAndOtherActionsAreUntouched()
    {
        var inner = ToolPolicy.From(r => r is FileWriteRequest ? ToolDecision.Reject("outside the working copy") : ToolDecision.Approve());
        var guarded = new RequiredReading([Notes]).Guard(inner);

        var write = await guarded.EvaluateAsync(new FileWriteRequest("/etc/hosts"), CancellationToken.None);
        var shell = await guarded.EvaluateAsync(new ShellRequest("cat src/A.cs"), CancellationToken.None);

        Assert.Equal(("outside the working copy", true), (write.Reason, write.CountsTowardRefusalLimit));
        Assert.Equal(ToolVerdict.Approve, shell.Verdict);
    }

    [Fact]
    public void WindowsPathsInJsonArgumentsCountAsRead()
    {
        var reading = new RequiredReading([@"C:\pkg\MIGRATION.md"]);

        reading.MarkRead("""{"path":"C:\\pkg\\MIGRATION.md"}""");

        Assert.Empty(reading.Unread);
    }
}
