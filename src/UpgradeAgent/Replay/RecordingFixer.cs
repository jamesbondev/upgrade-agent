using UpgradeAgent.Agent.Activities;
using UpgradeAgent.Infrastructure;
using UpgradeAgent.Run;

namespace UpgradeAgent.Replay;

internal sealed class RecordingFixer(IGroupFixer inner, Recording recording, AgentActivity activity, GitCli git, TimeProvider time) : IGroupFixer
{
    public async Task<FixOutcome> FixAsync(FixContext context, CancellationToken cancellationToken)
    {
        var worktree = context.WorktreePath;

        var snapshot = (await git.RunAsync(worktree, ["stash", "create"], cancellationToken)).Trim();

        var recorder = new ActivityRecorder(time);
        FixOutcome outcome;
        using (activity.Attach(recorder))
        {
            outcome = await inner.FixAsync(context, cancellationToken);
        }

        await git.RunAsync(worktree, ["add", "--all", "--intent-to-add"], cancellationToken);
        var patch = await git.RunAsync(worktree, ["diff", "--binary", snapshot.Length > 0 ? snapshot : "HEAD"], cancellationToken);
        await git.RunAsync(worktree, ["reset", "-q"], cancellationToken);

        recording.SaveGroup(context.Group.Name, ForSharing(recorder.Events, worktree), patch, outcome);
        return outcome;
    }

    public ValueTask DisposeAsync() => inner.DisposeAsync();

    private static List<RecordedActivity> ForSharing(IEnumerable<RecordedActivity> events, string worktree)
    {
        string Relative(string text) => RepoPath.RelativeInText(worktree, text);
        return events
            .Where(e => e.Event is not Transcript)
            .Select(e => e with
            {
                Event = e.Event switch
                {
                    Note note => note with { Text = Relative(note.Text) },
                    AgentMessage message => message with { Text = Relative(message.Text) },
                    ToolStarted tool => tool with { Detail = Relative(tool.Detail) },
                    ToolFailed failed => failed with { Error = Relative(failed.Error) },
                    ActionRefused refused => refused with { Action = Relative(refused.Action), Reason = Relative(refused.Reason) },
                    var other => other,
                },
            })
            .ToList();
    }
}
