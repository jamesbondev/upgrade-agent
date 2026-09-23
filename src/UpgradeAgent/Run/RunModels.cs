using UpgradeAgent.Agent;
using UpgradeAgent.Build;
using UpgradeAgent.Bumping;
using UpgradeAgent.Detection;
using UpgradeAgent.Guardrails;
using UpgradeAgent.Workspace;

namespace UpgradeAgent.Run;

public enum GroupStatus
{
    Accepted,
    Rejected,
    NothingToDo,
    Cancelled,
}

public sealed record GroupResult(
    string Name,
    GroupKind Kind,
    GroupStatus Status,
    string? Reason,
    string? Commit,
    IReadOnlyList<VersionEdit> Edits,
    IReadOnlyList<ManualUpdate> Manual,
    GuardrailReport? Guardrails,
    FixOutcome? Fix,
    TimeSpan Duration,
    IReadOnlyList<string>? BuildWarnings = null);

public sealed record RunReport(
    string RunId,
    string Branch,
    string WorktreePath,
    string TargetCommit,
    string SdkVersion,
    DateTimeOffset StartedUtc,
    TimeSpan Duration,
    UpgradePlan Plan,
    IReadOnlyList<GroupResult> Groups,
    IReadOnlyList<string> Ledger);

/// <summary>What the fixer gets when a group's build or tests fail after the bump.</summary>
public sealed record FixContext(
    RunWorkspace Workspace,
    UpdateGroup Group,
    BumpResult Bump,
    BuildResult Build,
    TestRunResult? Tests,
    IReadOnlyList<string>? TestArgs = null);

/// <param name="Attempted">False when no fixer ran (for example, the agent is disabled).</param>
/// <param name="Details">The agent's structured account of the group, when it produced one.</param>
public sealed record FixOutcome(bool Attempted, string Summary, GroupSummary? Details = null, AgentStats? Stats = null);

public sealed record AgentStats(
    string? Model,
    int ModelCalls,
    int ToolCalls,
    long InputTokens,
    long OutputTokens,
    double AiCredits,
    int OperatorApprovals,
    int Refusals,
    bool BudgetExceeded,
    TimeSpan Duration);

/// <summary>
/// Fixes a broken group. The app never trusts the fixer's claims: afterwards it rebuilds, retests
/// and runs the guardrails itself.
/// </summary>
public interface IGroupFixer
{
    Task<FixOutcome> FixAsync(FixContext context, CancellationToken cancellationToken);
}

public sealed class NoAgentFixer : IGroupFixer
{
    public Task<FixOutcome> FixAsync(FixContext context, CancellationToken cancellationToken) =>
        Task.FromResult(new FixOutcome(false, "no agent configured; the group needs code changes"));
}
