using UpgradeAgent.Publishing;
using UpgradeAgent.Run;
using UpgradeAgent.Tests.TestSupport;

namespace UpgradeAgent.Tests.Publishing;

public sealed class PushBranchToolTests : IAsyncLifetime
{
    private TempRepo _repo = null!;
    private string _verified = null!;

    public async Task InitializeAsync()
    {
        _repo = await TempRepo.CreateAsync();
        await _repo.Write("a.txt", "1").CommitAsync("base");
        _verified = await _repo.Write("a.txt", "2").CommitAsync("verified group");
    }

    [Fact]
    public async Task DryRunReportsWhatWouldBePushed()
    {
        var tool = Tool(accepted: true);

        var result = await tool.PushAsync(CancellationToken.None);

        Assert.Equal(PushOutcome.DryRun, result.Outcome);
        Assert.StartsWith("Dry run: would push agent/nuget-updates-20260923-1200 (1 verified commit(s))", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusesWhenHeadIsNotTheLastVerifiedCommit()
    {
        await _repo.Write("a.txt", "3").CommitAsync("unverified");

        var result = await Tool(accepted: true).PushAsync(CancellationToken.None);

        Assert.Equal(PushOutcome.Refused, result.Outcome);
        Assert.Contains("is not the last verified commit", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusesWithUncommittedChanges()
    {
        _repo.Write("a.txt", "dirty");

        Assert.Contains("uncommitted change", (await Tool(accepted: true).PushAsync(CancellationToken.None)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusesWhenNothingWasAccepted()
    {
        Assert.Contains("nothing to publish", (await Tool(accepted: false).PushAsync(CancellationToken.None)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunsOnlyOnce()
    {
        var destination = new CountingDestination();
        var tool = Tool(accepted: true, destination);

        await tool.PushBranchAsync();
        var second = await tool.PushBranchAsync();

        Assert.Equal(1, destination.Pushes);
        Assert.StartsWith("push_branch was already called", second, StringComparison.Ordinal);
    }

    public Task DisposeAsync()
    {
        _repo.Dispose();
        return Task.CompletedTask;
    }

    internal PushBranchTool Tool(bool accepted, IPushDestination? destination = null)
    {
        var groups = accepted ? [TestData.Group("patch-minor", GroupStatus.Accepted, _verified)] : Array.Empty<GroupResult>();
        return new PushBranchTool(_repo.Git, TestData.Report(groups: groups, worktree: _repo.Path), destination ?? new DryRunDestination(_repo.Git, _repo.Path));
    }

    private sealed class CountingDestination : IPushDestination
    {
        public int Pushes { get; private set; }

        public Task<string> DescribeAsync(CancellationToken cancellationToken) => Task.FromResult("test remote");

        public Task<PushResult> PushAsync(RunReport report, CancellationToken cancellationToken)
        {
            Pushes++;
            return Task.FromResult(PushResult.Success("pushed"));
        }
    }
}
