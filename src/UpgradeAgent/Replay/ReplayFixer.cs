using Spectre.Console;
using UpgradeAgent.Infrastructure;
using UpgradeAgent.Run;

namespace UpgradeAgent.Replay;

/// <summary>
/// Plays back a recorded agent session instead of calling the model: the recorded activity at a readable
/// pace, then the recorded patch. Everything after (rebuild, retest, guardrails, commit) runs for real.
/// </summary>
public sealed class ReplayFixer(Recording recording, IAnsiConsole console, object consoleLock, GitCli git, double maxGapSeconds) : IGroupFixer
{
    public async Task<FixOutcome> FixAsync(FixContext context, CancellationToken cancellationToken)
    {
        if (!recording.HasGroup(context.Group.Name))
        {
            return new FixOutcome(false, $"replay: recording '{recording.Name}' has no agent session for this group");
        }

        var (activity, patchPath, outcome) = recording.LoadGroup(context.Group.Name);
        lock (consoleLock)
        {
            console.MarkupLine($"  [black on yellow] REPLAY [/] [yellow]recorded agent session from '{Markup.Escape(recording.Name)}'; the checks below run live[/]");
        }

        var previous = 0.0;
        foreach (var line in activity)
        {
            var gap = Math.Clamp(line.OffsetSeconds - previous, 0, maxGapSeconds);
            previous = line.OffsetSeconds;
            if (gap > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(gap), cancellationToken);
            }

            lock (consoleLock)
            {
                console.MarkupLine(line.Markup);
            }
        }

        if (new FileInfo(patchPath).Length > 0)
        {
            var apply = await git.TryRunAsync(context.Workspace.WorktreePath, ["apply", "--whitespace=nowarn", patchPath], cancellationToken);
            if (!apply.Succeeded)
            {
                throw new RunAbortedException(
                    $"The recorded patch for '{context.Group.Name}' no longer applies (the repo or the plan changed since recording). Record again.\n{apply.CombinedOutput.Trim()}");
            }
        }

        return outcome with { Summary = $"replayed: {outcome.Summary}" };
    }
}
