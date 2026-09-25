namespace UpgradeAgent.Infrastructure;

internal static class RepoPath
{
    public static string Relative(string root, string path) => Normalize(Path.GetRelativePath(root, path));

    public static string Normalize(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.StartsWith("./", StringComparison.Ordinal) ? normalized[2..] : normalized;
    }

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

    public static string SafeFileName(string name) =>
        string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' ? c : '_'));
}
