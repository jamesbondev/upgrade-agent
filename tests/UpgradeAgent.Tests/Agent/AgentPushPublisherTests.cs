using AgentHarness.Policies;
using AgentHarness.Testing;
using UpgradeAgent.Agent;
using UpgradeAgent.Publishing;
using UpgradeAgent.Run;
using UpgradeAgent.Tests.TestSupport;

namespace UpgradeAgent.Tests.Agent;

public sealed class AgentPushPublisherTests : IAsyncLifetime
{
    private readonly List<(string Action, string Reason)> _asked = [];
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
    public async Task TheAgentsCallGoesToTheOperatorWithWhatWouldBePushed()
    {
        var backend = new ScriptedBackend().Turn(t => t.CallTool(PushBranchTool.Name).Reply("Pushed."));

        var result = await new AgentPushPublisher(backend, Prompter(approve: true)).PublishAsync(_tool, CancellationToken.None);

        Assert.Equal(PushOutcome.DryRun, result.Outcome);
        Assert.Same(result, _tool.Result);
        var (action, reason) = Assert.Single(_asked);
        Assert.Equal(await _tool.DescribeAsync(CancellationToken.None), action);
        Assert.Equal("The agent wants to publish. This leaves the machine and needs your approval.", reason);
        Assert.False(backend.LastSettings!.UseBuiltInTools);
        Assert.Equal([PushBranchTool.Name], backend.LastSettings.Tools.Select(t => t.Name));
    }

    [Fact]
    public async Task ADeclinedPushTellsTheAgentNotToRetry()
    {
        var backend = new ScriptedBackend().Turn(t => t.CallTool(PushBranchTool.Name).Reply("It was not pushed."));

        var result = await new AgentPushPublisher(backend, Prompter(approve: false)).PublishAsync(_tool, CancellationToken.None);

        Assert.Equal(PushOutcome.Declined, result.Outcome);
        Assert.Null(_tool.Result);
        Assert.Equal("The operator declined the push. Do not retry; reply that the branch was not pushed.", Assert.Single(backend.Decisions).Feedback);
    }

    [Fact]
    public async Task AnAgentThatNeverCallsTheToolPushesNothing()
    {
        var backend = new ScriptedBackend().Reply("I'd rather not.");

        var result = await new AgentPushPublisher(backend, Prompter(approve: true)).PublishAsync(_tool, CancellationToken.None);

        Assert.Equal(PushResult.Declined("Not pushed: the operator declined, or the agent did not call push_branch."), result);
        Assert.Empty(_asked);
    }

    public Task DisposeAsync()
    {
        _repo.Dispose();
        return Task.CompletedTask;
    }

    private IApprovalPrompter Prompter(bool approve) => ApprovalPrompter.From((action, reason) =>
    {
        _asked.Add((action, reason));
        return approve;
    });
}
