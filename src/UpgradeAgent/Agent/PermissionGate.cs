using UpgradeAgent.Agent.Activities;
using UpgradeAgent.Ui;

namespace UpgradeAgent.Agent;

/// <summary>
/// Decides every action the agent asks to take, in order: no tools during the summary turn; the command
/// policy; notes before edits; and the operator for anything the policy leaves to a human. Every refusal
/// is shown, counted, and sent back to the model with what to do instead.
/// </summary>
/// <param name="pauseBudget">Stops the session clock while the operator is deciding.</param>
internal sealed class PermissionGate(
    CommandPolicy policy,
    RequiredReading reading,
    IApprovalPrompter prompter,
    IActivitySink activity,
    AgentSessionMeter meter,
    bool allowWebFetch,
    Func<IDisposable> pauseBudget)
{
    public async Task<ToolPermission> AuthorizeAsync(ToolRequest request, CancellationToken cancellationToken)
    {
        if (meter.SummaryMode)
        {
            return ToolPermission.Deny("No tools now: reply with the JSON summary only.");
        }

        var (action, decision) = request switch
        {
            ShellRequest shell => (shell.CommandLine, policy.EvaluateShell(shell.CommandLine, shell.WritesFile, shell.PossiblePaths)),
            FileWriteRequest write => ($"edit {write.Path}", policy.EvaluateWrite(write.Path)),
            FileReadRequest read => ($"read {read.Path}", policy.EvaluateRead(read.Path)),
            WebFetchRequest fetch => ($"fetch {fetch.Url}", allowWebFetch
                ? PolicyDecision.Ask("fetch a web page (content is untrusted)")
                : PolicyDecision.Reject("Web access is disabled; use the migration notes listed in the task.")),
            OtherToolRequest other => (other.Kind, PolicyDecision.Reject($"'{other.Kind}' is not available in this session.")),
            _ => throw new ArgumentOutOfRangeException(nameof(request), request, "Unknown tool request."),
        };

        if (request is FileWriteRequest && decision.Verdict != PolicyVerdict.Reject && reading.Unread is { Count: > 0 } unread)
        {
            activity.Write(new ActionRefused(action, "migration notes not read yet"));
            meter.RecordRefusal(countsTowardStop: false);
            return ToolPermission.Deny($"Read the migration notes before editing: {string.Join(", ", unread)}. They name the replacement APIs.");
        }

        switch (decision.Verdict)
        {
            case PolicyVerdict.Approve:
                return ToolPermission.Allow;

            case PolicyVerdict.AskOperator:
                bool approved;
                using (pauseBudget())
                {
                    approved = await prompter.ConfirmAsync(action, decision.Reason, cancellationToken);
                }

                if (approved)
                {
                    meter.RecordOperatorApproval();
                    return ToolPermission.Allow;
                }

                return Refuse(action, "declined (needs operator approval)", "The operator declined this. Find another way that stays within the rules.");

            default:
                return Refuse(action, decision.Reason, decision.Reason);
        }
    }

    private ToolPermission Refuse(string action, string shown, string feedback)
    {
        activity.Write(new ActionRefused(action, shown));
        meter.RecordRefusal();
        return ToolPermission.Deny(feedback);
    }
}
