using UpgradeAgent.Infrastructure;

namespace UpgradeAgent.Workspace;

/// <summary>
/// Where a run happens. The worktree sits next to the target repo, never inside UpgradeAgent's folder:
/// MSBuild and NuGet read Directory.Build.*, Directory.Packages.props, nuget.config and global.json
/// from parent folders, so a worktree elsewhere could build differently from the real repo.
/// </summary>
public sealed record RunWorkspace(
    string RepoPath,
    string WorkRoot,
    string RunId,
    string BranchName,
    string WorktreePath,
    string SolutionPath,
    string OutputDirectory)
{
    private static readonly string[] InheritedConfigFiles =
    [
        "Directory.Build.props", "Directory.Build.targets", "Directory.Build.rsp", "Directory.Packages.props",
        "nuget.config", "NuGet.Config", "global.json", ".editorconfig",
    ];

    public static string DefaultWorkRoot(string repoPath) =>
        Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(repoPath))!, ".ua-work");

    public static async Task<RunWorkspace> CreateAsync(
        GitCli git, string repoPath, string solutionPath, string workRoot, string outputRoot, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var runId = now.UtcDateTime.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var branch = $"agent/nuget-updates-{now.UtcDateTime:yyyyMMdd-HHmm}";
        if (await git.BranchExistsAsync(repoPath, branch, cancellationToken))
        {
            branch = $"{branch}{now.UtcDateTime:ss}";
        }

        var worktree = Path.Combine(workRoot, runId);
        Directory.CreateDirectory(workRoot);
        await git.RunAsync(repoPath, ["worktree", "add", "-b", branch, worktree, "HEAD"], cancellationToken);

        var relativeSolution = Path.GetRelativePath(repoPath, solutionPath);
        return new RunWorkspace(
            repoPath, workRoot, runId, branch, worktree, Path.Combine(worktree, relativeSolution), Path.Combine(outputRoot, $"run-{runId}"));
    }

    /// <summary>Inherited config files that affect the worktree but not the original repo.</summary>
    public static IReadOnlyList<string> FindConfigLeaks(string repoPath, string worktreePath)
    {
        var original = InheritedConfigFilesAbove(repoPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return InheritedConfigFilesAbove(worktreePath).Where(f => !original.Contains(f)).ToList();
    }

    private static IEnumerable<string> InheritedConfigFilesAbove(string directory)
    {
        for (var current = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)));
             current is not null;
             current = Path.GetDirectoryName(current))
        {
            foreach (var name in InheritedConfigFiles)
            {
                var candidate = Path.Combine(current, name);
                if (File.Exists(candidate))
                {
                    yield return candidate;
                }
            }
        }
    }
}
