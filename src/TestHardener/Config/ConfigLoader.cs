using Microsoft.Extensions.Configuration;
using RepoKit;
using RepoKit.AzureDevOps;

namespace TestHardener.Config;

internal sealed record TargetConfig(string Name, string Project, IReadOnlyList<string> TestProjects, string? TestFilter, IReadOnlyList<string> Mutate, IReadOnlyList<string> IgnoreStringMutationsIn);

internal sealed record RepoTarget(string Name, AzureDevOpsRepo? AzureDevOps, string? LocalPath, string Solution, IReadOnlyList<TargetConfig> Targets)
{
    public static readonly IReadOnlyList<string> DefaultConventionFiles = ["AGENTS.md", "CLAUDE.md", ".github/copilot-instructions.md"];

    public IReadOnlyList<string> VerifyTestProjects { get; init; } = [];

    public IReadOnlyList<string> ConventionFiles { get; init; } = DefaultConventionFiles;

    public string? TestNamePattern { get; init; }

    public string Location => AzureDevOps?.WebUrl ?? LocalPath ?? Name;
}

internal sealed record ResolvedConfig(
    TestHardenerOptions Options,
    IReadOnlyList<RepoTarget> Repos,
    string OutputDirectory,
    string WorkRoot,
    string StrykerToolPath);

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
            .AddEnvironmentVariables("TESTHARDENER_")
            .Build();

        return (configuration, baseDirectory);
    }

    internal static ResolvedConfig Resolve(TestHardenerOptions options, string baseDirectory)
    {
        var repos = options.Repos.Select(repo => ResolveRepo(repo, options.AzureDevOps, baseDirectory)).ToList();
        var workRoot = string.IsNullOrWhiteSpace(options.Output.WorkRoot)
            ? RepoWorkspace.DefaultWorkRoot("test-hardener")
            : Path.GetFullPath(ExpandHome(options.Output.WorkRoot), baseDirectory);

        return new ResolvedConfig(
            options,
            repos,
            Path.GetFullPath(options.Output.Directory, Directory.GetCurrentDirectory()),
            workRoot,
            Path.GetFullPath(ExpandHome(options.Stryker.ToolPath), baseDirectory));
    }

    internal static string ExpandHome(string path) =>
        path == "~" || path.StartsWith("~/", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Length > 2 ? path[2..] : "")
            : path;

    internal static string RepoPath(string path) => path.Replace('\\', '/').TrimStart('/');

    internal static string TargetName(string project) => Path.GetFileNameWithoutExtension(RepoPath(project));

    private static RepoTarget ResolveRepo(RepoOptions repo, AzureDevOpsSettings defaults, string baseDirectory)
    {
        var targets = repo.Targets.Select(t => new TargetConfig(
            TargetName(t.Project),
            RepoPath(t.Project),
            t.TestProjects.Select(RepoPath).ToList(),
            string.IsNullOrWhiteSpace(t.TestFilter) ? null : t.TestFilter,
            t.Mutate,
            t.IgnoreStringMutationsIn)).ToList();
        var solution = RepoPath(repo.Solution);
        var local = !string.IsNullOrWhiteSpace(repo.Path);

        try
        {
            var organization = string.IsNullOrWhiteSpace(repo.OrganizationUrl) ? defaults.OrganizationUrl : repo.OrganizationUrl;
            var project = string.IsNullOrWhiteSpace(repo.Project) ? defaults.Project : repo.Project;
            return new RepoTarget(
                repo.Name,
                local ? null : new AzureDevOpsRepo(organization, project, repo.Name),
                local ? Path.GetFullPath(repo.Path!, baseDirectory) : null,
                solution,
                targets)
            {
                VerifyTestProjects = repo.VerifyTestProjects.Count > 0
                    ? repo.VerifyTestProjects.Select(RepoPath).ToList()
                    : targets.SelectMany(t => t.TestProjects).Distinct(StringComparer.Ordinal).ToList(),
                ConventionFiles = repo.ConventionFiles?.Select(RepoPath).ToList() ?? RepoTarget.DefaultConventionFiles,
                TestNamePattern = string.IsNullOrWhiteSpace(repo.TestNamePattern) ? null : repo.TestNamePattern,
            };
        }
        catch (ArgumentException ex)
        {
            throw new ConfigurationException($"Repo '{repo.Name}': {ex.Message}");
        }
    }
}

internal sealed class ConfigurationException(string message) : Exception(message);
