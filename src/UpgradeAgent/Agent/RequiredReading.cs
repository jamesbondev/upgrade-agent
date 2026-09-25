using AgentHarness;
using AgentHarness.Policies;

namespace UpgradeAgent.Agent;

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

    public IToolPolicy Guard(IToolPolicy inner) => inner.Wrap((request, decision) =>
        request is FileWriteRequest && decision.Verdict != ToolVerdict.Reject && Unread is { Count: > 0 } unread
            ? ToolDecision.Reject($"Read the migration notes before editing: {string.Join(", ", unread)}. They name the replacement APIs.")
                with { CountsTowardRefusalLimit = false, LogReason = "migration notes not read yet" }
            : decision);
}
