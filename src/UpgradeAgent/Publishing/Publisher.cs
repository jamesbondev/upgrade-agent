using UpgradeAgent.Ui;

namespace UpgradeAgent.Publishing;

/// <summary>Gets the operator's approval for push_branch and runs it.</summary>
internal interface IPushPublisher
{
    Task<PushResult> PublishAsync(PushBranchTool tool, CancellationToken cancellationToken);
}

/// <summary>
/// Used when no model is live (replay, --agent none): the app calls push_branch itself, behind the same
/// approval prompt the agent would trigger.
/// </summary>
internal sealed class DirectPushPublisher(IApprovalPrompter prompter) : IPushPublisher
{
    public async Task<PushResult> PublishAsync(PushBranchTool tool, CancellationToken cancellationToken)
    {
        var action = await tool.DescribeAsync(cancellationToken);
        if (!await prompter.ConfirmAsync(action, "Publishing leaves the machine and needs your approval.", cancellationToken))
        {
            return new PushResult(false, false, "Not pushed: the operator declined (or nobody could approve in non-interactive mode).");
        }

        await tool.PushBranchAsync(cancellationToken);
        return tool.Result!;
    }
}
