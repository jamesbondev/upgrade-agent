using UpgradeAgent.Publishing;
using UpgradeAgent.Run;
using UpgradeAgent.Tests.TestSupport;
using UpgradeAgent.Ui;

namespace UpgradeAgent.Tests.Publishing;

public sealed class DirectPushPublisherTests : IAsyncLifetime
{
    private TempRepo _repo = null!;
    private PushBranchTool _tool = null!;

    public async Task InitializeAsync()
    {
        _repo = await TempRepo.CreateAsync();
        var verified = await _repo.Write("a.txt", "1").CommitAsync("verified group");
        var report = TestData.Report(groups: [TestData.Group("patch-minor", GroupStatus.Accepted, verified)], worktree: _repo.Path);
        _tool = new PushBranchTool(_repo.Git, report, new DryRunDestination(_repo.Git, _repo.Path));
    }

    [Fact]
    public async Task DeclinedMeansNotPushed()
    {
        var result = await new DirectPushPublisher(new DeclineAllPrompter()).PublishAsync(_tool, CancellationToken.None);

        Assert.Equal(PushOutcome.Declined, result.Outcome);
        Assert.Null(_tool.Result);
    }

    [Fact]
    public async Task ApprovedRunsPushBranch()
    {
        var result = await new DirectPushPublisher(new ApproveAll()).PublishAsync(_tool, CancellationToken.None);

        Assert.Equal(PushOutcome.DryRun, result.Outcome);
        Assert.Same(result, _tool.Result);
    }

    public Task DisposeAsync()
    {
        _repo.Dispose();
        return Task.CompletedTask;
    }

    private sealed class ApproveAll : IApprovalPrompter
    {
        public Task<bool> ConfirmAsync(string action, string reason, CancellationToken cancellationToken) => Task.FromResult(true);
    }
}
