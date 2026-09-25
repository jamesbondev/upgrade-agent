using System.ComponentModel;
using UpgradeAgent.Infrastructure;
using UpgradeAgent.Run;

namespace UpgradeAgent.Publishing;

internal enum PushOutcome
{
    Pushed,
    DryRun,
    Declined,
    Refused,
    Failed,
}

internal sealed record PushResult(PushOutcome Outcome, string Message)
{
    public bool Pushed => Outcome == PushOutcome.Pushed;

    public static PushResult Success(string message) => new(PushOutcome.Pushed, message);

    public static PushResult DryRun(string message) => new(PushOutcome.DryRun, message);

    public static PushResult Declined(string message) => new(PushOutcome.Declined, message);

    public static PushResult Refused(string message) => new(PushOutcome.Refused, message);

    public static PushResult Failed(string message) => new(PushOutcome.Failed, message);
}

/// <summary>Where a verified branch goes: a real remote, or nowhere (dry run).</summary>
internal interface IPushDestination
{
    Task<string> DescribeAsync(CancellationToken cancellationToken);

    Task<PushResult> PushAsync(RunReport report, CancellationToken cancellationToken);
}

/// <summary>The default: says what would be pushed where, and pushes nothing.</summary>
internal sealed class DryRunDestination(GitCli git, string worktree) : IPushDestination
{
    public async Task<string> DescribeAsync(CancellationToken cancellationToken)
    {
        var result = await git.TryRunAsync(worktree, ["remote", "get-url", "origin"], cancellationToken);
        return $"{(result.Succeeded ? result.StandardOutput.Trim() : "origin (none configured in this repo)")} · dry run";
    }

    public async Task<PushResult> PushAsync(RunReport report, CancellationToken cancellationToken) =>
        PushResult.DryRun(
            $"Dry run: would push {report.Branch} ({report.Ledger.Count} verified commit(s)) to {await DescribeAsync(cancellationToken)}. Nothing left the machine.");
}

/// <summary>
/// The only way a run's branch leaves the machine. It is exposed to the agent as an approval-required
/// tool, and it checks for itself that what it pushes is exactly what the guardrails verified:
/// HEAD must be the last commit in the ledger and the worktree must be clean. It runs at most once.
/// </summary>
internal sealed class PushBranchTool(GitCli git, RunReport report, IPushDestination destination)
{
    public const string Name = "push_branch";

    public string WorktreePath => report.WorktreePath;

    public string Branch => report.Branch;

    /// <summary>What the one call did, or null if it hasn't been made.</summary>
    public PushResult? Result { get; private set; }

    [Description("Publishes the verified upgrade branch to the remote. Call it once, after all groups are finished. Takes no arguments.")]
    public async Task<string> PushBranchAsync(CancellationToken cancellationToken = default) => (await PushAsync(cancellationToken)).Message;

    public async Task<PushResult> PushAsync(CancellationToken cancellationToken)
    {
        if (Result is not null)
        {
            return Result with { Message = $"push_branch was already called: {Result.Message}" };
        }

        Result = await ExecuteAsync(cancellationToken);
        return Result;
    }

    public async Task<string> DescribeAsync(CancellationToken cancellationToken) =>
        $"{Name}: {report.Branch} → {await destination.DescribeAsync(cancellationToken)} · {report.Ledger.Count} verified commit(s)";

    private async Task<PushResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        if (report.Ledger.Count == 0)
        {
            return PushResult.Refused("Refused: no group was accepted, so there is nothing to publish.");
        }

        var head = await git.HeadAsync(report.WorktreePath, cancellationToken);
        if (head != report.Ledger[^1])
        {
            return PushResult.Refused($"Refused: HEAD {head.ShortSha()} is not the last verified commit {report.Ledger[^1].ShortSha()}.");
        }

        var changes = (await git.StatusAsync(report.WorktreePath, includeIgnored: false, cancellationToken)).Count;
        return changes > 0
            ? PushResult.Refused($"Refused: the worktree has {changes} uncommitted change(s).")
            : await destination.PushAsync(report, cancellationToken);
    }
}
