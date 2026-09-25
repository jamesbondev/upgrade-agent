using Microsoft.Extensions.Configuration;
using UpgradeAgent.Workspace;

namespace UpgradeAgent.Config;

/// <summary>Configuration with every path resolved to an absolute path.</summary>
/// <param name="WorkRoot">Run worktrees and the baseline cache.</param>
internal sealed record ResolvedConfig(
    UpgradeAgentOptions Options,
    string RepoPath,
    string SolutionPath,
    string OutputDirectory,
    string WorkRoot,
    string RecordingsDirectory);

internal static class ConfigLoader
{
    /// <summary>
    /// Layers appsettings.json next to the executable, then the optional <paramref name="configPath"/>,
    /// user secrets and <c>UPGRADEAGENT_</c> environment variables, in increasing precedence.
    /// </summary>
    /// <returns>The configuration, and the folder relative paths in it resolve against.</returns>
    public static (IConfiguration Configuration, string BaseDirectory) Load(string? configPath)
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true);

        var baseDirectory = Directory.GetCurrentDirectory();
        if (configPath is not null)
        {
            var fullConfigPath = Path.GetFullPath(configPath);
            builder.AddJsonFile(fullConfigPath, optional: false);
            baseDirectory = Path.GetDirectoryName(fullConfigPath)!;
        }

        var configuration = builder
            .AddUserSecrets(typeof(ConfigLoader).Assembly, optional: true)
            .AddEnvironmentVariables("UPGRADEAGENT_")
            .Build();

        return (configuration, baseDirectory);
    }

    /// <summary>
    /// Target paths resolve against the config file's folder, the work root against the repo, and output
    /// folders against the current directory.
    /// </summary>
    internal static ResolvedConfig Resolve(UpgradeAgentOptions options, string baseDirectory)
    {
        var repoPath = Path.GetFullPath(options.Target.RepoPath, baseDirectory);
        if (!Directory.Exists(repoPath))
        {
            throw new ConfigurationException($"Target repo not found: {repoPath}");
        }

        var solutionPath = string.IsNullOrWhiteSpace(options.Target.Solution)
            ? FindSingleSolution(repoPath)
            : Path.GetFullPath(options.Target.Solution, repoPath);

        var workRoot = string.IsNullOrWhiteSpace(options.Target.WorkRoot)
            ? RunWorkspace.DefaultWorkRoot(repoPath)
            : Path.GetFullPath(options.Target.WorkRoot, repoPath);

        var currentDirectory = Directory.GetCurrentDirectory();
        return new ResolvedConfig(
            options,
            repoPath,
            solutionPath,
            Path.GetFullPath(options.Output.Directory, currentDirectory),
            workRoot,
            Path.GetFullPath(options.Output.RecordingsDirectory, currentDirectory));
    }

    private static string FindSingleSolution(string repoPath)
    {
        var solutions = Directory.EnumerateFiles(repoPath, "*.sln")
            .Concat(Directory.EnumerateFiles(repoPath, "*.slnx"))
            .ToList();

        return solutions.Count switch
        {
            1 => solutions[0],
            0 => throw new ConfigurationException($"No .sln or .slnx found in {repoPath}. Set Target:Solution."),
            _ => throw new ConfigurationException($"Several solutions found in {repoPath}. Set Target:Solution to one of: {string.Join(", ", solutions.Select(Path.GetFileName))}"),
        };
    }
}

internal sealed class ConfigurationException(string message) : Exception(message);
