using UpgradeAgent.Detection;
using UpgradeAgent.Publishing;
using UpgradeAgent.Run;
using UpgradeAgent.Tests.TestSupport;
using UpgradeAgent.Ui;

namespace UpgradeAgent.Tests.Publishing;

public sealed class PushBranchToolTests : IDisposable
{
    private readonly TempRepo _repo = new();
    private readonly string _verified;

    public PushBranchToolTests()
    {
        _repo.Write("a.txt", "1").Commit("base");
        _verified = _repo.Write("a.txt", "2").Commit("verified group");
    }

    [Fact]
    public async Task DryRunReportsWhatWouldBePushed()
    {
        var tool = Tool([_verified]);

        var message = await tool.PushBranchAsync();

        Assert.StartsWith("Dry run: would push agent/test (1 verified commit(s))", message, StringComparison.Ordinal);
        Assert.False(tool.Result!.Refused);
    }

    [Fact]
    public async Task RefusesWhenHeadIsNotTheLastVerifiedCommit()
    {
        _repo.Write("a.txt", "3").Commit("unverified");

        var message = await Tool([_verified]).PushBranchAsync();

        Assert.Contains("is not the last verified commit", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusesWithUncommittedChanges()
    {
        _repo.Write("a.txt", "dirty");

        Assert.Contains("uncommitted change", await Tool([_verified]).PushBranchAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusesWhenNothingWasAccepted()
    {
        Assert.Contains("nothing to publish", await Tool([]).PushBranchAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunsOnlyOnce()
    {
        var pushes = 0;
        var tool = Tool([_verified], dryRun: false, push: _ => { pushes++; return Task.FromResult(new PushResult(true, false, "pushed")); });

        await tool.PushBranchAsync();
        var second = await tool.PushBranchAsync();

        Assert.Equal(1, pushes);
        Assert.StartsWith("push_branch was already called", second, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DirectPublisherPushesOnlyAfterApproval()
    {
        var declined = await new DirectPushPublisher(new DeclineAllPrompter()).PublishAsync(Tool([_verified]), CancellationToken.None);
        var approved = await new DirectPushPublisher(new ApproveAll()).PublishAsync(Tool([_verified]), CancellationToken.None);

        Assert.StartsWith("Not pushed: the operator declined", declined.Message, StringComparison.Ordinal);
        Assert.StartsWith("Dry run:", approved.Message, StringComparison.Ordinal);
    }

    public void Dispose() => _repo.Dispose();

    private PushBranchTool Tool(IReadOnlyList<string> ledger, bool dryRun = true, Func<CancellationToken, Task<PushResult>>? push = null) =>
        new(_repo.Git, Report(ledger), dryRun, push);

    private RunReport Report(IReadOnlyList<string> ledger) =>
        new("run", "agent/test", _repo.Path, "base", "10.0.112", DateTimeOffset.UtcNow, TimeSpan.Zero,
            new UpgradePlan(DateTimeOffset.UtcNow, "x.slnx", [], []), [], ledger);

    private sealed class ApproveAll : IApprovalPrompter
    {
        public Task<bool> ConfirmAsync(string action, string reason, CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
