using RepoKit.AzureDevOps;

namespace ReadmeChecker.Config;

internal sealed class ReadmeCheckerOptions
{
    public AzureDevOpsSettings AzureDevOps { get; set; } = new();

    public List<RepoOptions> Repos { get; set; } = [];

    public ReadmeOptions Readme { get; set; } = new();

    public AgentOptions Agent { get; set; } = new();

    public OutputOptions Output { get; set; } = new();

    public PublishOptions Publish { get; set; } = new();
}

internal sealed class AzureDevOpsSettings
{
    public string OrganizationUrl { get; set; } = "";

    public string Project { get; set; } = "";

    public string PatEnvVar { get; set; } = "ADO_PAT";

    public string AccessTokenEnvVar { get; set; } = "SYSTEM_ACCESSTOKEN";

    public string? Pat { get; set; }

    public bool UseAzureIdentity { get; set; } = true;

    public AzureDevOpsAuthOptions ToAuthOptions() => new()
    {
        PatEnvVar = PatEnvVar,
        AccessTokenEnvVar = AccessTokenEnvVar,
        Pat = Pat,
        UseAzureIdentity = UseAzureIdentity,
    };
}

internal sealed class RepoOptions
{
    public string Name { get; set; } = "";

    public string? OrganizationUrl { get; set; }

    public string? Project { get; set; }

    public string? Path { get; set; }

    public string? Readme { get; set; }

    public ReadmeDepth? Depth { get; set; }
}

internal enum ReadmeDepth
{
    Quick,
    Deep,
}

internal sealed class ReadmeOptions
{
    public ReadmeDepth Depth { get; set; } = ReadmeDepth.Quick;

    public int RecentCommits { get; set; } = 20;

    public int MaxCandidates { get; set; } = 25;

    public double MinKeptRatio { get; set; } = 0.5;
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

    public int MaxMinutes { get; set; } = 5;

    public int MaxToolCalls { get; set; } = 40;

    public int MaxRefusals { get; set; } = 5;

    public bool SkipWhenClean { get; set; } = true;

    public double MaxAiCreditsPerRun { get; set; }

    public string? GitHubTokenEnvVar { get; set; }

    public List<string> RemoveEnvironmentVariables { get; set; } = [];
}

internal sealed class OutputOptions
{
    public string Directory { get; set; } = "out";

    public string? WorkRoot { get; set; }

    public bool KeepClones { get; set; }

    public int CloneTimeoutMinutes { get; set; } = 5;
}

internal sealed class PublishOptions
{
    public string BranchPrefix { get; set; } = "agent/readme-refresh-";

    public string Label { get; set; } = "agent-generated";

    public int CooldownDays { get; set; } = 30;

    public string CommitName { get; set; } = "ReadmeChecker";

    public string CommitEmail { get; set; } = "readme-checker@localhost";
}
