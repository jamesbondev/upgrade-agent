using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Agents.AI.GitHub.Copilot;
using Microsoft.Extensions.AI;
using UpgradeAgent.Config;
using UpgradeAgent.Publishing;
using UpgradeAgent.Ui;

namespace UpgradeAgent.Agent.Copilot;

/// <summary>
/// Publishing with a live agent: a short session whose only tool is push_branch, wrapped in
/// <see cref="ApprovalRequiredAIFunction"/>. The framework turns the call into a permission request, which
/// goes to the operator. The tool still checks for itself that it pushes only what the guardrails verified.
/// </summary>
internal sealed class CopilotPushPublisher(CopilotClientHost host, AgentOptions options, IApprovalPrompter prompter) : IPushPublisher
{
    /// <summary>For the agent's turn only: the clock stops while the operator decides.</summary>
    private static readonly TimeSpan AgentTurnTimeout = TimeSpan.FromMinutes(2);

    public string How => "the agent must call push_branch; you approve it";

    public async Task<PushResult> PublishAsync(PushBranchTool tool, CancellationToken cancellationToken)
    {
        var client = await host.GetAsync(tool.WorktreePath, cancellationToken);
        var action = await tool.DescribeAsync(cancellationToken);
        using var timeout = new PausableTimeout(AgentTurnTimeout, TimeProvider.System);
        using var turn = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        var config = CopilotSessions.Hardened(options, tool.WorktreePath);
        config.Tools = [new ApprovalRequiredAIFunction(AIFunctionFactory.Create(tool.PushBranchAsync, PushBranchTool.Name))];
        config.AvailableTools = [PushBranchTool.Name];
        config.OnPermissionRequest = async (request, _) =>
        {
            var toolName = request switch
            {
                PermissionRequestCustomTool custom => custom.ToolName,
                PermissionRequestHook hook => hook.ToolName,
                _ => null,
            };

            if (toolName != PushBranchTool.Name)
            {
                return PermissionDecision.Reject("Only push_branch is available in this session.");
            }

            using (timeout.Pause())
            {
                return await prompter.ConfirmAsync(action, "The agent wants to publish. This leaves the machine and needs your approval.", cancellationToken)
                    ? PermissionDecision.ApproveOnce()
                    : PermissionDecision.Reject("The operator declined the push. Do not retry; reply that the branch was not pushed.");
            }
        };

        await using var agent = new GitHubCopilotAgent(client, config, ownsClient: false, name: CopilotSessions.ClientName);
        try
        {
            await agent.RunAsync(
                $"Every package group is finished and independently verified on branch {tool.Branch}. " +
                "Publish it by calling push_branch exactly once, then reply with one sentence saying whether it was pushed.",
                cancellationToken: turn.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The agent ran out of time; the tool's own result says whether anything was pushed.
        }

        return tool.Result ?? PushResult.Declined("Not pushed: the operator declined, or the agent did not call push_branch.");
    }
}
