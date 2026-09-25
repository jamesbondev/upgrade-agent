namespace UpgradeAgent.MsBuild;

/// <summary>What MSBuild and NuGet read, in one place: file kinds and the config files inherited from parent folders.</summary>
internal static class MsBuildFiles
{
    /// <summary>Config that decides how packages restore and projects build, picked up from the project's folder or any parent.</summary>
    public static readonly IReadOnlySet<string> BuildConfig = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Directory.Build.props", "Directory.Build.targets", "Directory.Build.rsp", "Directory.Packages.props", "nuget.config", "global.json",
    };

    /// <summary><see cref="BuildConfig"/> plus analyzer settings: everything a folder's parents can change about a build.</summary>
    public static readonly IReadOnlySet<string> InheritedConfig = new HashSet<string>(BuildConfig, StringComparer.OrdinalIgnoreCase) { ".editorconfig" };

    /// <summary>Files that can hold a package's version for a project, nearest first after the project itself.</summary>
    public static readonly IReadOnlyList<string> VersionGoverning = ["Directory.Packages.props", "Directory.Build.props", "Directory.Build.targets"];

    public static bool IsProjectFile(string path) => Path.GetExtension(path).ToLowerInvariant() is ".csproj" or ".fsproj" or ".vbproj";

    public static bool IsMsBuildFile(string path) => IsProjectFile(path) || Path.GetExtension(path).ToLowerInvariant() is ".props" or ".targets";

    public static bool IsSourceFile(string path) => Path.GetExtension(path).ToLowerInvariant() is ".cs" or ".fs" or ".vb";

    /// <summary>The nearest <paramref name="name"/> in <paramref name="startDirectory"/> or its parents, not above <paramref name="stopAt"/>.</summary>
    public static string? FindNearest(string startDirectory, string name, string stopAt)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stopAt));
        for (var directory = startDirectory; directory is not null && directory.Length >= root.Length; directory = Path.GetDirectoryName(directory))
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Every file under <paramref name="root"/>, skipping build output, VCS and tool folders.</summary>
    public static IEnumerable<string> EnumerateRepoFiles(string root)
    {
        var pending = new Stack<string>([root]);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                yield return file;
            }

            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if (Path.GetFileName(child) is not ("bin" or "obj" or ".git" or "node_modules" or "TestResults"))
                {
                    pending.Push(child);
                }
            }
        }
    }
}
