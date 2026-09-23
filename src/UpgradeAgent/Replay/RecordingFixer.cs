using UpgradeAgent.Agent;
using UpgradeAgent.Infrastructure;
using UpgradeAgent.Run;

namespace UpgradeAgent.Replay;

/// <summary>
/// Wraps the real fixer and saves what it did: the activity lines, and a patch of only the agent's
/// changes (diffed against a snapshot taken after the app's bump, so replay can bump and then apply).
/// </summary>
public sealed class RecordingFixer(IGroupFixer inner, Recording recording, AgentActivityRenderer activity, GitCli git) : IGroupFixer, IAsyncDisposable
{
    public async Task<FixOutcome> FixAsync(FixContext context, CancellationToken cancellationToken)
    {
        var worktree = context.Workspace.WorktreePath;

        // A commit object for the post-bump tree, taken without touching the working tree or index.
        var snapshot = (await git.RunAsync(worktree, ["stash", "create"], cancellationToken)).Trim();

        activity.BeginCapture();
        FixOutcome outcome;
        try
        {
            outcome = await inner.FixAsync(context, cancellationToken);
        }
        finally
        {
            activity.EndCapture();
        }

        // Worktree paths differ per run and machine; keep recordings relative.
        var root = Path.TrimEndingDirectorySeparator(worktree);
        var lines = activity.LastCapture
            .Select(l => l with { Markup = l.Markup.Replace(root + Path.DirectorySeparatorChar, "", StringComparison.Ordinal).Replace(root, ".", StringComparison.Ordinal) })
            .ToList();

        // Intent-to-add makes the agent's new files show up in the diff; the index is reset right after.
        await git.RunAsync(worktree, ["add", "--all", "--intent-to-add"], cancellationToken);
        var patch = await git.RunAsync(worktree, ["diff", "--binary", snapshot.Length > 0 ? snapshot : "HEAD"], cancellationToken);
        await git.RunAsync(worktree, ["reset", "-q"], cancellationToken);

        recording.SaveGroup(context.Group.Name, lines, patch, outcome);
        return outcome;
    }

    public ValueTask DisposeAsync() => inner is IAsyncDisposable disposable ? disposable.DisposeAsync() : ValueTask.CompletedTask;
}
