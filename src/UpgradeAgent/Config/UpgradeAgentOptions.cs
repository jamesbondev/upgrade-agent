using UpgradeAgent.Detection;

namespace UpgradeAgent.Config;

internal sealed class UpgradeAgentOptions
{
    public TargetOptions Target { get; set; } = new();

    public PolicyOptions Policy { get; set; } = new();

    public OutputOptions Output { get; set; } = new();

    public AgentOptions Agent { get; set; } = new();

    public AzureDevOpsOptions AzureDevOps { get; set; } = new();
}

internal sealed class AzureDevOpsOptions
{
    public string OrganizationUrl { get; set; } = "";

    public string Project { get; set; } = "";

    public string Repository { get; set; } = "";

    public string PatEnvVar { get; set; } = "ADO_PAT";

    public string AccessTokenEnvVar { get; set; } = "SYSTEM_ACCESSTOKEN";

    public string? Pat { get; set; }

    public bool UseAzureIdentity { get; set; } = true;

    public string Label { get; set; } = "agent-generated";

    public bool IsConfigured => OrganizationUrl.Length > 0 && Project.Length > 0 && Repository.Length > 0;
}

internal enum AgentProvider
{
    Copilot,

    None,
}

internal sealed class AgentOptions
{
    public AgentProvider Provider { get; set; } = AgentProvider.Copilot;

    public string? Model { get; set; }

    public string? ReasoningEffort { get; set; }

    public int MaxMinutesPerGroup { get; set; } = 10;

    public int MaxToolCallsPerGroup { get; set; } = 80;

    public int MaxRefusalsPerGroup { get; set; } = 5;

    public int MaxBuildsWithoutProgress { get; set; } = 3;

    public int MaxErrorsForAgent { get; set; } = 50;

    public bool AllowWebFetch { get; set; }

    public List<string> RemoveEnvironmentVariables { get; set; } = [];

    public string? GitHubTokenEnvVar { get; set; }
}

internal sealed class TargetOptions
{
    public string RepoPath { get; set; } = "";

    public string Solution { get; set; } = "";

    public string? WorkRoot { get; set; }

    public bool RunGitHooks { get; set; } = true;

    public List<string> TestArgs { get; set; } = [];
}

internal sealed class PolicyOptions
{
    public List<string> Allow { get; set; } = [];

    public List<DenyRule> Deny { get; set; } = [];

    public BumpKind MaxAutoBump { get; set; } = BumpKind.Major;

    public bool AttemptMajors { get; set; } = true;

    public int MaxMajorJump { get; set; } = 2;

    public bool IncludePrerelease { get; set; }

    public Dictionary<string, List<string>>? Groups { get; set; }

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

internal sealed class DenyRule
{
    public string Id { get; set; } = "";

    public string? Reason { get; set; }
}

internal sealed class OutputOptions
{
    public string Directory { get; set; } = "out";

    public string RecordingsDirectory { get; set; } = "recordings";
}
