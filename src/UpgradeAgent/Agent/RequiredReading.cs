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
            _unread.RemoveWhere(path => toolText.Contains(path, StringComparison.Ordinal));
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
}
