using UpgradeAgent.Agent.Activities;
using UpgradeAgent.Infrastructure;
using UpgradeAgent.Run;

namespace UpgradeAgent.Replay;

/// <summary>
/// Wraps the real fixer and saves what it did: the activity events, and a patch of only the agent's
/// changes (diffed against a snapshot taken after the app's bump, so replay can bump and then apply).
/// </summary>
internal sealed class RecordingFixer(IGroupFixer inner, Recording recording, AgentActivity activity, GitCli git, TimeProvider time) : IGroupFixer
{
    public async Task<FixOutcome> FixAsync(FixContext context, CancellationToken cancellationToken)
    {
        var worktree = context.WorktreePath;

        // A commit object for the post-bump tree, taken without touching the working tree or index.
        var snapshot = (await git.RunAsync(worktree, ["stash", "create"], cancellationToken)).Trim();

        var recorder = new ActivityRecorder(time);
        FixOutcome outcome;
        using (activity.Attach(recorder))
        {
            outcome = await inner.FixAsync(context, cancellationToken);
        }

        // Intent-to-add makes the agent's new files show up in the diff; the index is reset right after.
        await git.RunAsync(worktree, ["add", "--all", "--intent-to-add"], cancellationToken);
        var patch = await git.RunAsync(worktree, ["diff", "--binary", snapshot.Length > 0 ? snapshot : "HEAD"], cancellationToken);
        await git.RunAsync(worktree, ["reset", "-q"], cancellationToken);

        recording.SaveGroup(context.Group.Name, recorder.Events, patch, outcome);
        return outcome;
    }

    public ValueTask DisposeAsync() => inner.DisposeAsync();
}
