using System.Globalization;
using UpgradeAgent.Infrastructure;
using UpgradeAgent.MsBuild;

namespace UpgradeAgent.Workspace;

/// <summary>
/// Where a run happens. The worktree sits next to the target repo, never inside UpgradeAgent's folder:
/// MSBuild and NuGet read Directory.Build.*, Directory.Packages.props, nuget.config and global.json
/// from parent folders, so a worktree elsewhere could build differently from the real repo.
/// </summary>
internal sealed record RunWorkspace(
    string RepoPath,
    string WorkRoot,
    string RunId,
    string BranchName,
    string WorktreePath,
    string SolutionPath,
    string OutputDirectory)
{
    public static string DefaultWorkRoot(string repoPath) =>
        Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(repoPath))!, ".ua-work");

    /// <summary>Names the run, its branch and folders. Creates nothing, so checks can run before anything exists.</summary>
    public static async Task<RunWorkspace> PlanAsync(
        GitCli git, string repoPath, string solutionPath, string workRoot, string outputRoot, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var runId = now.UtcDateTime.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var branch = string.Create(CultureInfo.InvariantCulture, $"agent/nuget-updates-{now.UtcDateTime:yyyyMMdd-HHmm}");
        if (await git.BranchExistsAsync(repoPath, branch, cancellationToken))
        {
            branch = string.Create(CultureInfo.InvariantCulture, $"{branch}{now.UtcDateTime:ss}");
        }

        var worktree = Path.Combine(workRoot, runId);
        return new RunWorkspace(
            repoPath, workRoot, runId, branch, worktree, Path.Combine(worktree, Path.GetRelativePath(repoPath, solutionPath)), Path.Combine(outputRoot, $"run-{runId}"));
    }

    /// <summary>Adds the worktree on a new branch from HEAD, and the output folder.</summary>
    public async Task CreateAsync(GitCli git, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(WorkRoot);
        Directory.CreateDirectory(OutputDirectory);
        await git.RunAsync(RepoPath, ["worktree", "add", "-b", BranchName, WorktreePath, "HEAD"], cancellationToken);
    }

    /// <summary>Inherited config files that would affect the worktree but not the original repo.</summary>
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
            // Matched case-insensitively against the files that actually exist: probing "nuget.config" and
            // "NuGet.Config" separately finds the same file twice on case-insensitive file systems (Windows).
            foreach (var file in FilesIn(current).Where(f => MsBuildFiles.InheritedConfig.Contains(Path.GetFileName(f))))
            {
                yield return file;
            }
        }
    }

    private static IEnumerable<string> FilesIn(string directory)
    {
        try
        {
            return Directory.Exists(directory) ? Directory.GetFiles(directory) : [];
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            // Folders we can't list (e.g. system folders above the repo) can't contribute config.
            return [];
        }
    }
}
