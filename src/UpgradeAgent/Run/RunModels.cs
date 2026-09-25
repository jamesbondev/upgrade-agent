using UpgradeAgent.Agent;
using UpgradeAgent.Build;
using UpgradeAgent.Bumping;
using UpgradeAgent.Detection;
using UpgradeAgent.Guardrails;

namespace UpgradeAgent.Run;

internal enum GroupStatus
{
    Accepted,
    Rejected,
    NothingToDo,
    Cancelled,
}

internal static class GroupStatusLabels
{
    /// <summary>The words for an outcome, shared by the console and the PR description.</summary>
    public static string Label(this GroupStatus status) => status switch
    {
        GroupStatus.Accepted => "accepted",
        GroupStatus.Rejected => "rejected",
        GroupStatus.Cancelled => "cancelled",
        _ => "nothing to do",
    };
}

internal sealed record GroupResult(
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
    IReadOnlyList<string>? BuildWarnings = null)
{
    /// <summary>"Id from → to" per distinct version step.</summary>
    public IReadOnlyList<string> Changes => Edits.Select(e => $"{e.Id} {e.From} → {e.To}").Distinct().ToList();
}

/// <param name="OutputDirectory">This run's folder under Output:Directory (out/run-&lt;id&gt;).</param>
internal sealed record RunReport(
    string RunId,
    string Branch,
    string WorktreePath,
    string OutputDirectory,
    string TargetCommit,
    string SdkVersion,
    DateTimeOffset StartedUtc,
    TimeSpan Duration,
    UpgradePlan Plan,
    IReadOnlyList<GroupResult> Groups)
{
    /// <summary>The verified commits, in order: the only thing push_branch may publish.</summary>
    public IReadOnlyList<string> Ledger => Groups.Where(g => g is { Status: GroupStatus.Accepted, Commit: not null }).Select(g => g.Commit!).ToList();

    /// <summary>The group a planned update ran in, if it ran.</summary>
    public GroupResult? GroupFor(PlannedUpdate update) =>
        update.Group is null ? null : Groups.FirstOrDefault(g => string.Equals(g.Name, update.Group, StringComparison.OrdinalIgnoreCase));
}

/// <summary>What the fixer gets when a group's build or tests fail after the bump.</summary>
/// <param name="OutputDirectory">The run's output folder, for the agent log.</param>
/// <param name="TestArgs">Target:TestArgs: the agent must run exactly the tests the guardrails compare.</param>
internal sealed record FixContext(
    string WorktreePath,
    string SolutionPath,
    string OutputDirectory,
    UpdateGroup Group,
    BuildResult Build,
    TestRunResult? Tests,
    IReadOnlyList<string> TestArgs);

/// <param name="Attempted">False when no fixer ran (for example, the agent is disabled), so nothing can have changed.</param>
/// <param name="Details">The agent's structured account of the group, when it produced one.</param>
/// <param name="Replayed">Played back from a recording rather than a live model.</param>
internal sealed record FixOutcome(bool Attempted, string Summary, GroupSummary? Details = null, AgentStats? Stats = null, bool Replayed = false);

internal sealed record AgentStats(
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
/// and runs the guardrails itself. Disposed at the end of the run (a live agent holds a runtime).
/// </summary>
internal interface IGroupFixer : IAsyncDisposable
{
    Task<FixOutcome> FixAsync(FixContext context, CancellationToken cancellationToken);
}

internal sealed class NoAgentFixer : IGroupFixer
{
    public Task<FixOutcome> FixAsync(FixContext context, CancellationToken cancellationToken) =>
        Task.FromResult(new FixOutcome(false, "no agent configured; the group needs code changes"));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
