using UpgradeAgent.Detection;

namespace UpgradeAgent.Config;

public sealed class UpgradeAgentOptions
{
    public TargetOptions Target { get; set; } = new();

    public PolicyOptions Policy { get; set; } = new();

    public OutputOptions Output { get; set; } = new();

    public AgentOptions Agent { get; set; } = new();

    public AzureDevOpsOptions AzureDevOps { get; set; } = new();
}

public sealed class AzureDevOpsOptions
{
    /// <summary>For example https://dev.azure.com/contoso.</summary>
    public string OrganizationUrl { get; set; } = "";

    public string Project { get; set; } = "";

    public string Repository { get; set; } = "";

    /// <summary>Environment variable holding a PAT (scope: Code read &amp; write).</summary>
    public string PatEnvVar { get; set; } = "ADO_PAT";

    /// <summary>Environment variable holding a bearer token, e.g. a pipeline's System.AccessToken.</summary>
    public string AccessTokenEnvVar { get; set; } = "SYSTEM_ACCESSTOKEN";

    /// <summary>A PAT from user secrets (<c>dotnet user-secrets set AzureDevOps:Pat ...</c>). Never put it in appsettings.json.</summary>
    public string? Pat { get; set; }

    /// <summary>Fall back to Azure CLI / DefaultAzureCredential (Entra) when no PAT or token is set.</summary>
    public bool UseAzureIdentity { get; set; } = true;

    public string Label { get; set; } = "agent-generated";

    public bool IsConfigured => OrganizationUrl.Length > 0 && Project.Length > 0 && Repository.Length > 0;
}

public sealed class AgentOptions
{
    /// <summary><c>copilot</c> (GitHub Copilot via the local Copilot CLI login) or <c>none</c>.</summary>
    public string Provider { get; set; } = "copilot";

    /// <summary>Copilot model ID; null lets Copilot choose.</summary>
    public string? Model { get; set; }

    public string? ReasoningEffort { get; set; }

    public int MaxMinutesPerGroup { get; set; } = 10;

    public int MaxToolCallsPerGroup { get; set; } = 80;

    /// <summary>Let the agent fetch URLs (release notes). Off by default: fetched pages are untrusted input.</summary>
    public bool AllowWebFetch { get; set; }

    /// <summary>Extra environment variables to hide from the agent, on top of the built-in secret patterns.</summary>
    public List<string> RemoveEnvironmentVariables { get; set; } = [];

    /// <summary>Environment variable holding a GitHub token for Copilot (pipelines). Empty means the local Copilot CLI login.</summary>
    public string? GitHubTokenEnvVar { get; set; }
}

public sealed class TargetOptions
{
    /// <summary>Path to the target git repo. Relative paths resolve against the config file's folder.</summary>
    public string RepoPath { get; set; } = "";

    /// <summary>Solution (.sln/.slnx) relative to <see cref="RepoPath"/>. Empty means the single solution in the repo root.</summary>
    public string Solution { get; set; } = "";

    /// <summary>Folder for run worktrees and the baseline cache. Default: <c>.ua-work</c> beside the repo. Relative paths resolve against the repo.</summary>
    public string? WorkRoot { get; set; }

    /// <summary>Extra arguments passed through to <c>dotnet test</c>, e.g. a filter.</summary>
    public List<string> TestArgs { get; set; } = [];
}

public sealed class PolicyOptions
{
    /// <summary>Package ID globs. When non-empty, only matching packages are updated.</summary>
    public List<string> Allow { get; set; } = [];

    public List<DenyRule> Deny { get; set; } = [];

    public BumpKind MaxAutoBump { get; set; } = BumpKind.Major;

    public bool AttemptMajors { get; set; } = true;

    public bool IncludePrerelease { get; set; }

    /// <summary>Package families that must move together, as name → ID globs. Null means <see cref="DefaultGroups"/>.</summary>
    public Dictionary<string, List<string>>? Groups { get; set; }

    /// <summary>Exact final target version per package ID. Pins demos and replays against new releases.</summary>
    public Dictionary<string, string> TargetOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, List<string>> DefaultGroups { get; } = new Dictionary<string, List<string>>
    {
        ["efcore"] = ["Microsoft.EntityFrameworkCore*", "Npgsql.EntityFrameworkCore.*", "Pomelo.EntityFrameworkCore.*"],
        ["aspnetcore"] = ["Microsoft.AspNetCore.*"],
        ["extensions"] = ["Microsoft.Extensions.*"],
        ["xunit"] = ["xunit", "xunit.*"],
        ["opentelemetry"] = ["OpenTelemetry*"],
        ["serilog"] = ["Serilog*"],
        ["polly"] = ["Polly*", "Microsoft.Extensions.Http.Polly"],
    };

    public IReadOnlyDictionary<string, List<string>> EffectiveGroups => Groups ?? DefaultGroups;
}

public sealed class DenyRule
{
    public string Id { get; set; } = "";

    public string? Reason { get; set; }
}

public sealed class OutputOptions
{
    /// <summary>Folder for plans, reports and PR descriptions. Relative paths resolve against the current directory.</summary>
    public string Directory { get; set; } = "out";

    /// <summary>Folder for record/replay sessions. Relative paths resolve against the current directory.</summary>
    public string RecordingsDirectory { get; set; } = "recordings";
}
