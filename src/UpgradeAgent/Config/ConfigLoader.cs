using Microsoft.Extensions.Configuration;

namespace UpgradeAgent.Config;

/// <summary>Configuration with every path resolved to an absolute path.</summary>
public sealed record ResolvedConfig(UpgradeAgentOptions Options, string RepoPath, string SolutionPath, string OutputDirectory);

public static class ConfigLoader
{
    /// <summary>
    /// Loads appsettings.json next to the executable, then the optional <paramref name="configPath"/>,
    /// user secrets and <c>UPGRADEAGENT_</c> environment variables, in increasing precedence.
    /// </summary>
    public static ResolvedConfig Load(string? configPath)
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true);

        string baseDirectory = Directory.GetCurrentDirectory();
        if (configPath is not null)
        {
            var fullConfigPath = Path.GetFullPath(configPath);
            if (!File.Exists(fullConfigPath))
            {
                throw new ConfigurationException($"Config file not found: {fullConfigPath}");
            }

            builder.AddJsonFile(fullConfigPath, optional: false);
            baseDirectory = Path.GetDirectoryName(fullConfigPath)!;
        }

        var configuration = builder
            .AddUserSecrets(typeof(ConfigLoader).Assembly, optional: true)
            .AddEnvironmentVariables("UPGRADEAGENT_")
            .Build();

        var options = new UpgradeAgentOptions();
        configuration.Bind(options);

        return Resolve(options, baseDirectory);
    }

    internal static ResolvedConfig Resolve(UpgradeAgentOptions options, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(options.Target.RepoPath))
        {
            throw new ConfigurationException("Target:RepoPath is not set. Pass --config <file> or set UPGRADEAGENT_Target__RepoPath.");
        }

        var repoPath = Path.GetFullPath(options.Target.RepoPath, baseDirectory);
        if (!Directory.Exists(repoPath))
        {
            throw new ConfigurationException($"Target repo not found: {repoPath}");
        }

        var solutionPath = string.IsNullOrWhiteSpace(options.Target.Solution)
            ? FindSingleSolution(repoPath)
            : Path.GetFullPath(options.Target.Solution, repoPath);

        var outputDirectory = Path.GetFullPath(options.Output.Directory, Directory.GetCurrentDirectory());

        return new ResolvedConfig(options, repoPath, solutionPath, outputDirectory);
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

public sealed class ConfigurationException(string message) : Exception(message);
