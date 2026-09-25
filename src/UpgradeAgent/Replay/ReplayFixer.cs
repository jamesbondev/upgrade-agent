using Spectre.Console;
using UpgradeAgent.Agent.Activities;
using UpgradeAgent.Infrastructure;
using UpgradeAgent.Run;
using UpgradeAgent.Ui;

namespace UpgradeAgent.Replay;

internal sealed class ReplayFixer(Recording recording, AgentActivity activity, SynchronizedConsole console, GitCli git, double maxGapSeconds, TimeProvider time)
    : IGroupFixer
{
    public async Task<FixOutcome> FixAsync(FixContext context, CancellationToken cancellationToken)
    {
        if (!recording.HasGroup(context.Group.Name))
        {
            return new FixOutcome(false, $"replay: recording '{recording.Name}' has no agent session for this group");
        }

        var session = recording.LoadGroup(context.Group.Name);
        console.Write(c => c.MarkupLine(
            $"  [black on yellow] REPLAY [/] [yellow]recorded agent session from '{Markup.Escape(recording.Name)}'; the checks below run live[/]"));

        using var log = new FileActivitySink(Path.Combine(context.OutputDirectory, "agent", $"{RepoPath.SafeFileName(context.Group.Name)}.log"), time);
        using (activity.Attach(log))
        {
            var previous = 0.0;
            foreach (var recorded in session.Activity)
            {
                var gap = Math.Clamp(recorded.OffsetSeconds - previous, 0, maxGapSeconds);
                previous = recorded.OffsetSeconds;
                if (gap > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(gap), time, cancellationToken);
                }

                activity.Write(recorded.Event);
            }
        }

        if (new FileInfo(session.PatchPath).Length > 0)
        {
            var apply = await git.TryRunAsync(context.WorktreePath, ["apply", "--whitespace=nowarn", session.PatchPath], cancellationToken);
            if (!apply.Succeeded)
            {
                throw new RunAbortedException(
                    $"The recorded patch for '{context.Group.Name}' no longer applies (the repo or the plan changed since recording). Record again.\n{apply.CombinedOutput.Trim()}");
            }
        }

        return session.Outcome with { Replayed = true };
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
