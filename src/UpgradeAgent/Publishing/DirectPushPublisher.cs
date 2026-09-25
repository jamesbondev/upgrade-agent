using AgentHarness.Policies;

namespace UpgradeAgent.Publishing;

internal sealed class DirectPushPublisher(IApprovalPrompter prompter) : IPushPublisher
{
    public string How => "push_branch needs your approval";

    public async Task<PushResult> PublishAsync(PushBranchTool tool, CancellationToken cancellationToken)
    {
        var action = await tool.DescribeAsync(cancellationToken);
        return await prompter.ConfirmAsync(action, "Publishing leaves the machine and needs your approval.", cancellationToken)
            ? await tool.PushAsync(cancellationToken)
            : PushResult.Declined("Not pushed: the operator declined (or nobody could approve in non-interactive mode).");
    }
}
