namespace ReadmeChecker.Detection;

internal static class RepoPaths
{
    public static string FolderOf(string path) => path.Contains('/', StringComparison.Ordinal) ? path[..path.LastIndexOf('/')] : "";

    public static string? Normalize(string path)
    {
        var parts = new List<string>();
        foreach (var segment in path.Replace('\\', '/').Split('/'))
        {
            switch (segment)
            {
                case "" or ".":
                    continue;
                case "..":
                    if (parts.Count == 0)
                    {
                        return null;
                    }

                    parts.RemoveAt(parts.Count - 1);
                    break;
                default:
                    parts.Add(segment);
                    break;
            }
        }

        return string.Join('/', parts);
    }

    public static string Join(string folder, string path) => folder.Length == 0 ? path : $"{folder}/{path}";
}
