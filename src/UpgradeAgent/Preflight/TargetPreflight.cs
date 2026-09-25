using System.Xml.Linq;
using UpgradeAgent.Config;
using UpgradeAgent.Infrastructure;
using UpgradeAgent.Build;

namespace UpgradeAgent.Preflight;

internal sealed record PreflightCheck(string Name, bool Passed, string Detail);

/// <summary>Cheap checks that fail fast, before any restore, detection or model call.</summary>
internal sealed class TargetPreflight(IProcessRunner processRunner)
{
    /// <param name="requireCleanRepo">A run branches from HEAD; uncommitted changes would silently be left out.</param>
    public async Task<IReadOnlyList<PreflightCheck>> RunAsync(ResolvedConfig config, bool requireCleanRepo, CancellationToken cancellationToken)
    {
        var checks = new List<PreflightCheck>
        {
            await CheckSdkAsync(config.RepoPath, cancellationToken),
            File.Exists(config.SolutionPath)
                ? new PreflightCheck("Solution", true, Path.GetRelativePath(config.RepoPath, config.SolutionPath))
                : new PreflightCheck("Solution", false, $"not found: {config.SolutionPath}"),
            CheckProjectStyle(config.RepoPath),
        };

        if (requireCleanRepo)
        {
            checks.Add(await CheckGitAsync(config.RepoPath, cancellationToken));
            checks.Add(await CheckUncommittedConfigAsync(config.RepoPath, cancellationToken));
        }

        return checks;
    }

    /// <summary>
    /// A run works in a git worktree, which only contains committed files. A local, uncommitted nuget.config
    /// (common for private feeds) would silently be missing there and restores would fail.
    /// </summary>
    internal async Task<PreflightCheck> CheckUncommittedConfigAsync(string repoPath, CancellationToken cancellationToken)
    {
        const string Name = "Build config in worktree";
        var git = new GitCli(processRunner);
        var listing = await git.TryRunAsync(repoPath, ["ls-files"], cancellationToken);
        if (!listing.Succeeded)
        {
            return new PreflightCheck(Name, false, "could not list tracked files");
        }

        var tracked = GitCli.SplitLines(listing.StandardOutput).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = MsBuildFiles.EnumerateRepoFiles(repoPath)
            .Where(f => MsBuildFiles.BuildConfig.Contains(Path.GetFileName(f)))
            .Select(f => RepoPath.Relative(repoPath, f))
            .Where(f => !tracked.Contains(f))
            .Order(StringComparer.Ordinal)
            .ToList();

        return missing.Count == 0
            ? new PreflightCheck(Name, true, "all build and NuGet config files are committed")
            : new PreflightCheck(Name, false,
                $"{string.Join(", ", missing)} exist(s) but aren't committed, so the run's worktree won't have them. " +
                "Commit them, or move them to the folder above the repo (the repo and .ua-work both inherit from there), " +
                "or put feeds in your user-level NuGet config.");
    }

    private async Task<PreflightCheck> CheckGitAsync(string repoPath, CancellationToken cancellationToken)
    {
        var git = new GitCli(processRunner);
        var head = await git.TryRunAsync(repoPath, ["rev-parse", "--short", "HEAD"], cancellationToken);
        if (!head.Succeeded)
        {
            return new PreflightCheck("Git repo", false, "not a git repository with at least one commit");
        }

        var changes = (await git.StatusAsync(repoPath, includeIgnored: false, cancellationToken))
            .Where(l => !l.StartsWith("??", StringComparison.Ordinal))
            .ToList();

        return changes.Count == 0
            ? new PreflightCheck("Git repo", true, $"clean at {head.StandardOutput.Trim()}")
            : new PreflightCheck("Git repo", false, $"{changes.Count} uncommitted change(s); commit or stash them first");
    }

    private async Task<PreflightCheck> CheckSdkAsync(string repoPath, CancellationToken cancellationToken)
    {
        // Run in the repo so its global.json decides which SDK is selected.
        var result = await processRunner.RunAsync("dotnet", ["--version"], repoPath, DotnetCli.BaseEnvironment, cancellationToken);
        var version = result.StandardOutput.Trim();
        if (!result.Succeeded)
        {
            return new PreflightCheck(".NET SDK", false, result.CombinedOutput.Trim());
        }

        return int.TryParse(version.Split('.')[0], out var major) && major >= 10
            ? new PreflightCheck(".NET SDK", true, version)
            : new PreflightCheck(".NET SDK", false, $"{version} (need 10.0 or later for 'dotnet package list')");
    }

    internal static PreflightCheck CheckProjectStyle(string repoPath)
    {
        var files = MsBuildFiles.EnumerateRepoFiles(repoPath).ToList();

        var packagesConfig = files.Where(f => string.Equals(Path.GetFileName(f), "packages.config", StringComparison.OrdinalIgnoreCase)).ToList();
        if (packagesConfig.Count > 0)
        {
            return new PreflightCheck("SDK-style projects", false,
                $"packages.config is not supported: {string.Join(", ", packagesConfig.Select(f => Path.GetRelativePath(repoPath, f)))}");
        }

        var projects = files.Where(MsBuildFiles.IsProjectFile).ToList();
        var legacy = projects.Where(p => !IsSdkStyle(p)).ToList();

        return legacy.Count == 0
            ? new PreflightCheck("SDK-style projects", true, projects.Count == 1 ? "1 project" : $"{projects.Count} projects")
            : new PreflightCheck("SDK-style projects", false,
                $"non-SDK-style projects are not supported: {string.Join(", ", legacy.Select(f => Path.GetRelativePath(repoPath, f)))}");
    }

    private static bool IsSdkStyle(string projectPath)
    {
        try
        {
            var root = XDocument.Load(projectPath).Root;
            return root?.Attribute("Sdk") is not null
                || root?.Elements().Any(e => e.Name.LocalName == "Sdk") == true
                || root?.Elements().Any(e => e.Name.LocalName == "Import" && (string?)e.Attribute("Sdk") is not null) == true;
        }
        catch (System.Xml.XmlException)
        {
            return false;
        }
    }
}
