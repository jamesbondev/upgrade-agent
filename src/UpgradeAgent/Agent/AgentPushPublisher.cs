using AgentHarness;
using AgentHarness.Policies;
using UpgradeAgent.Publishing;

namespace UpgradeAgent.Agent;

internal sealed class AgentPushPublisher(IAgentBackend backend, IApprovalPrompter prompter) : IPushPublisher
{
    private static readonly TimeSpan AgentTurnTimeout = TimeSpan.FromMinutes(2);

    public string How => "the agent must call push_branch; you approve it";

    public async Task<PushResult> PublishAsync(PushBranchTool tool, CancellationToken cancellationToken)
    {
        var action = await tool.DescribeAsync(cancellationToken);
        var policy = ToolPolicy.From(request => request is CustomToolRequest { Name: PushBranchTool.Name }
            ? ToolDecision.Ask(
                "The agent wants to publish. This leaves the machine and needs your approval.",
                prompt: action,
                declinedFeedback: "The operator declined the push. Do not retry; reply that the branch was not pushed.")
            : ToolDecision.Reject("Only push_branch is available in this session."));

        await using var session = await new AgentRunner(backend, prompter).StartAsync(new AgentSessionOptions
        {
            Name = "publish",
            WorkingDirectory = tool.WorktreePath,
            Policy = policy,
            Tools = [AgentTool.Create(tool.PushBranchAsync, PushBranchTool.Name, requiresApproval: true)],
            UseBuiltInTools = false,
            Limits = AgentLimits.None with { MaxDuration = AgentTurnTimeout },
        }, cancellationToken);

        await session.SendAsync(
            $"Every package group is finished and independently verified on branch {tool.Branch}. " +
            "Publish it by calling push_branch exactly once, then reply with one sentence saying whether it was pushed.",
            cancellationToken);

        return tool.Result ?? PushResult.Declined("Not pushed: the operator declined, or the agent did not call push_branch.");
    }
}
