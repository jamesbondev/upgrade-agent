using System.Xml.Linq;
using UpgradeAgent.Config;
using UpgradeAgent.Infrastructure;

namespace UpgradeAgent.Preflight;

public sealed record PreflightCheck(string Name, bool Passed, string Detail);

/// <summary>Cheap checks that fail fast, before any restore, detection or model call.</summary>
public sealed class TargetPreflight(IProcessRunner processRunner)
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
        }

        return checks;
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
        var result = await processRunner.RunAsync("dotnet", ["--version"], repoPath, cancellationToken: cancellationToken);
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
        var files = EnumerateRepoFiles(repoPath).ToList();

        var packagesConfig = files.Where(f => string.Equals(Path.GetFileName(f), "packages.config", StringComparison.OrdinalIgnoreCase)).ToList();
        if (packagesConfig.Count > 0)
        {
            return new PreflightCheck("SDK-style projects", false,
                $"packages.config is not supported: {string.Join(", ", packagesConfig.Select(f => Path.GetRelativePath(repoPath, f)))}");
        }

        var projects = files.Where(f => f.EndsWith("proj", StringComparison.OrdinalIgnoreCase)
            && Path.GetExtension(f) is ".csproj" or ".fsproj" or ".vbproj").ToList();
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

    private static IEnumerable<string> EnumerateRepoFiles(string repoPath)
    {
        var pending = new Stack<string>([repoPath]);
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
