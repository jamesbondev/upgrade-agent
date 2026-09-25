using AgentHarness;
using AgentHarness.Policies;

namespace UpgradeAgent.Agent;

/// <summary>
/// Migration notes too large to paste into the task. Edits are refused until each has been read; a tool call
/// that mentions the file's path (a view, cat or grep) counts as reading it.
/// </summary>
internal sealed class RequiredReading(IEnumerable<string> paths)
{
    private readonly Lock _lock = new();
    private readonly HashSet<string> _unread = new(paths, StringComparer.Ordinal);

    public void MarkRead(string? toolText)
    {
        if (toolText is null)
        {
            return;
        }

        lock (_lock)
        {
            // Arguments arrive as JSON, where Windows paths have doubled backslashes.
            _unread.RemoveWhere(path => toolText.Contains(path, StringComparison.Ordinal)
                || toolText.Contains(path.Replace("\\", "\\\\", StringComparison.Ordinal), StringComparison.Ordinal));
        }
    }

    public IReadOnlyList<string> Unread
    {
        get
        {
            lock (_lock)
            {
                return [.. _unread];
            }
        }
    }

    /// <summary>
    /// <paramref name="inner"/>, except that edits it would allow (or ask about) are refused while notes are unread.
    /// The refusal is a nudge, not a sign the agent is lost, so it doesn't count toward the refusal limit.
    /// </summary>
    public IToolPolicy Guard(IToolPolicy inner) => inner.Wrap((request, decision) =>
        request is FileWriteRequest && decision.Verdict != ToolVerdict.Reject && Unread is { Count: > 0 } unread
            ? ToolDecision.Reject($"Read the migration notes before editing: {string.Join(", ", unread)}. They name the replacement APIs.")
                with { CountsTowardRefusalLimit = false, LogReason = "migration notes not read yet" }
            : decision);
}
