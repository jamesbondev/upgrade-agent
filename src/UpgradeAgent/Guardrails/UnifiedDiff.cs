namespace UpgradeAgent.Guardrails;

public sealed record FileDiff(string Path, IReadOnlyList<string> Added, IReadOnlyList<string> Removed, bool IsNew, bool IsDeleted);

/// <summary>Parses <c>git diff -U0</c> output.</summary>
public static class UnifiedDiff
{
    public static IReadOnlyList<FileDiff> Parse(string diff)
    {
        var files = new List<FileDiff>();
        string? path = null;
        List<string> added = [], removed = [];
        bool isNew = false, isDeleted = false;

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
                (added, removed, isNew, isDeleted) = ([], [], false, false);
            }
            else if (path is null)
            {
                continue;
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
            else if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                continue;
            }
            else if (line.StartsWith('+'))
            {
                added.Add(line[1..]);
            }
            else if (line.StartsWith('-'))
            {
                removed.Add(line[1..]);
            }
        }

        Flush();
        return files;
    }

    private static string ParseHeaderPath(string header)
    {
        // "diff --git a/x b/x": good enough for the fallback; "+++ b/x" refines it.
        var bIndex = header.LastIndexOf(" b/", StringComparison.Ordinal);
        return bIndex > 0 ? header[(bIndex + 3)..] : header["diff --git ".Length..];
    }

    private static string StripPrefix(string path) =>
        path.StartsWith("b/", StringComparison.Ordinal) || path.StartsWith("a/", StringComparison.Ordinal) ? path[2..] : path;
}
