using Microsoft.Extensions.Configuration;
using RepoKit;
using RepoKit.AzureDevOps;

namespace ReadmeChecker.Config;

internal sealed record RepoTarget(string Name, AzureDevOpsRepo? AzureDevOps, string? LocalPath, string? ReadmePath, ReadmeDepth? Depth = null)
{
    public string Location => AzureDevOps?.WebUrl ?? LocalPath ?? Name;
}

internal sealed record ResolvedConfig(
    ReadmeCheckerOptions Options,
    IReadOnlyList<RepoTarget> Repos,
    string OutputDirectory,
    string WorkRoot);

internal static class ConfigLoader
{
    public static (IConfiguration Configuration, string BaseDirectory) Load(string? configPath)
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true);

        var baseDirectory = Directory.GetCurrentDirectory();
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
            .AddEnvironmentVariables("READMECHECKER_")
            .Build();

        return (configuration, baseDirectory);
    }

    internal static ResolvedConfig Resolve(ReadmeCheckerOptions options, string baseDirectory)
    {
        var repos = options.Repos.Select(repo => ResolveRepo(repo, options.AzureDevOps, baseDirectory)).ToList();
        var currentDirectory = Directory.GetCurrentDirectory();
        var workRoot = string.IsNullOrWhiteSpace(options.Output.WorkRoot)
            ? RepoWorkspace.DefaultWorkRoot("readme-checker")
            : Path.GetFullPath(options.Output.WorkRoot, baseDirectory);

        return new ResolvedConfig(options, repos, Path.GetFullPath(options.Output.Directory, currentDirectory), workRoot);
    }

    private static RepoTarget ResolveRepo(RepoOptions repo, AzureDevOpsSettings defaults, string baseDirectory)
    {
        var readme = string.IsNullOrWhiteSpace(repo.Readme) ? null : repo.Readme.Replace('\\', '/').TrimStart('/');
        if (!string.IsNullOrWhiteSpace(repo.Path))
        {
            return new RepoTarget(repo.Name, null, Path.GetFullPath(repo.Path, baseDirectory), readme, repo.Depth);
        }

        try
        {
            var organization = string.IsNullOrWhiteSpace(repo.OrganizationUrl) ? defaults.OrganizationUrl : repo.OrganizationUrl;
            var project = string.IsNullOrWhiteSpace(repo.Project) ? defaults.Project : repo.Project;
            return new RepoTarget(repo.Name, new AzureDevOpsRepo(organization, project, repo.Name), null, readme, repo.Depth);
        }
        catch (ArgumentException ex)
        {
            throw new ConfigurationException($"Repo '{repo.Name}': {ex.Message}");
        }
    }
}

internal sealed class ConfigurationException(string message) : Exception(message);
