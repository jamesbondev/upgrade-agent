namespace UpgradeAgent.Infrastructure;

/// <summary>Repository-relative paths, always with forward slashes, as git and the plan store them.</summary>
internal static class RepoPath
{
    public static string Relative(string root, string path) => Normalize(Path.GetRelativePath(root, path));

    /// <summary>Forward slashes, no leading <c>./</c>.</summary>
    public static string Normalize(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.StartsWith("./", StringComparison.Ordinal) ? normalized[2..] : normalized;
    }

    /// <summary>Replaces <paramref name="root"/> in free text (commands, log lines) with relative paths.</summary>
    public static string RelativeInText(string root, string text)
    {
        if (root.Length == 0)
        {
            return text;
        }

        var trimmed = Path.TrimEndingDirectorySeparator(root);
        return text
            .Replace(trimmed + Path.DirectorySeparatorChar, "", StringComparison.Ordinal)
            .Replace(trimmed, ".", StringComparison.Ordinal);
    }

    /// <summary>A name safe for a file or folder: letters, digits, '.' and '-' kept, anything else '_'.</summary>
    public static string SafeFileName(string name) =>
        string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_'));
}
