namespace UpgradeAgent.Guardrails;

internal sealed record FileDiff(string Path, IReadOnlyList<string> Added, IReadOnlyList<string> Removed, bool IsNew, bool IsDeleted)
{
    public IEnumerable<string> GenuinelyAdded => Unmatched(Added, Removed);

    public IEnumerable<string> GenuinelyRemoved => Unmatched(Removed, Added);

    private static IEnumerable<string> Unmatched(IReadOnlyList<string> lines, IReadOnlyList<string> counterparts)
    {
        var remaining = counterparts
            .Select(l => l.Trim())
            .GroupBy(l => l, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        foreach (var line in lines)
        {
            var key = line.Trim();
            if (remaining.TryGetValue(key, out var count) && count > 0)
            {
                remaining[key] = count - 1;
                continue;
            }

            yield return line;
        }
    }
}

internal static class UnifiedDiff
{
    public static IReadOnlyList<FileDiff> Parse(string diff)
    {
        var files = new List<FileDiff>();
        string? path = null;
        List<string> added = [], removed = [];
        bool isNew = false, isDeleted = false, inHunk = false;

        void Flush()
        {
            if (path is not null)
            {
                files.Add(new FileDiff(path, added, removed, isNew, isDeleted));
            }
        }

        foreach (var rawLine in diff.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                Flush();
                path = ParseHeaderPath(line);
                (added, removed, isNew, isDeleted, inHunk) = ([], [], false, false, false);
            }
            else if (path is null)
            {
                continue;
            }
            else if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                inHunk = true;
            }
            else if (inHunk)
            {
                if (line.StartsWith('+'))
                {
                    added.Add(line[1..]);
                }
                else if (line.StartsWith('-'))
                {
                    removed.Add(line[1..]);
                }
            }
            else if (line.StartsWith("new file mode", StringComparison.Ordinal))
            {
                isNew = true;
            }
            else if (line.StartsWith("deleted file mode", StringComparison.Ordinal))
            {
                isDeleted = true;
            }
            else if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                if (line != "+++ /dev/null")
                {
                    path = StripPrefix(line[4..]);
                }
            }
        }

        Flush();
        return files;
    }

    private static string ParseHeaderPath(string header)
    {
        var bIndex = header.LastIndexOf(" b/", StringComparison.Ordinal);
        return bIndex > 0 ? header[(bIndex + 3)..] : header["diff --git ".Length..];
    }

    private static string StripPrefix(string path) =>
        path.StartsWith("b/", StringComparison.Ordinal) || path.StartsWith("a/", StringComparison.Ordinal) ? path[2..] : path;
}
