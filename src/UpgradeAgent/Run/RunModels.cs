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
    public IReadOnlyList<string> Changes => Edits.Select(e => $"{e.Id} {e.From} → {e.To}").Distinct().ToList();
}

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
    public IReadOnlyList<string> Ledger => Groups.Where(g => g is { Status: GroupStatus.Accepted, Commit: not null }).Select(g => g.Commit!).ToList();

    public GroupResult? GroupFor(PlannedUpdate update) =>
        update.Group is null ? null : Groups.FirstOrDefault(g => string.Equals(g.Name, update.Group, StringComparison.OrdinalIgnoreCase));
}

internal sealed record FixContext(
    string WorktreePath,
    string SolutionPath,
    string OutputDirectory,
    UpdateGroup Group,
    BuildResult Build,
    TestRunResult? Tests,
    IReadOnlyList<string> TestArgs);

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
