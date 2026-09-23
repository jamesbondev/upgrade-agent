using System.ComponentModel;
using UpgradeAgent.Infrastructure;
using UpgradeAgent.Run;

namespace UpgradeAgent.Publishing;

public sealed record PushResult(bool Pushed, bool Refused, string Message);

/// <summary>
/// The only way a run's branch leaves the machine. It is exposed to the agent as an approval-required
/// tool, and it checks for itself that what it pushes is exactly what the guardrails verified:
/// HEAD must be the last commit in the ledger and the worktree must be clean.
/// </summary>
public sealed class PushBranchTool(GitCli git, RunReport report, bool dryRun, Func<CancellationToken, Task<PushResult>>? push = null)
{
    public const string Name = "push_branch";

    public PushResult? Result { get; private set; }

    [Description("Publishes the verified upgrade branch to the remote. Call it once, after all groups are finished. Takes no arguments.")]
    public async Task<string> PushBranchAsync(CancellationToken cancellationToken = default)
    {
        if (Result is not null)
        {
            return $"push_branch was already called: {Result.Message}";
        }

        Result = await ExecuteAsync(cancellationToken);
        return Result.Message;
    }

    public async Task<string> DescribeAsync(CancellationToken cancellationToken)
    {
        var remote = await RemoteAsync(cancellationToken);
        return $"{Name}: {report.Branch} → {remote ?? "(no remote)"} · {report.Ledger.Count} verified commit(s)";
    }

    private async Task<PushResult> ExecuteAsync(CancellationToken cancellationToken)
    {
        var worktree = report.WorktreePath;
        if (report.Ledger.Count == 0)
        {
            return new PushResult(false, true, "Refused: no group was accepted, so there is nothing to publish.");
        }

        var head = await git.HeadAsync(worktree, cancellationToken);
        if (head != report.Ledger[^1])
        {
            return new PushResult(false, true, $"Refused: HEAD {head[..8]} is not the last verified commit {report.Ledger[^1][..8]}.");
        }

        var changes = (await git.StatusAsync(worktree, includeIgnored: false, cancellationToken)).Count;
        if (changes > 0)
        {
            return new PushResult(false, true, $"Refused: the worktree has {changes} uncommitted change(s).");
        }

        var remote = await RemoteAsync(cancellationToken);
        if (dryRun || push is null)
        {
            return new PushResult(false, false,
                $"Dry run: would push {report.Branch} ({report.Ledger.Count} verified commit(s)) to {remote ?? "origin (none configured in this repo)"}. Nothing left the machine.");
        }

        return await push(cancellationToken);
    }

    private async Task<string?> RemoteAsync(CancellationToken cancellationToken)
    {
        var result = await git.TryRunAsync(report.WorktreePath, ["remote", "get-url", "origin"], cancellationToken);
        return result.Succeeded ? result.StandardOutput.Trim() : null;
    }
}
