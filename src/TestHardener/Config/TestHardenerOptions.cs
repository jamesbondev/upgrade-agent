using RepoKit.AzureDevOps;

namespace TestHardener.Config;

internal sealed class TestHardenerOptions
{
    public AzureDevOpsSettings AzureDevOps { get; set; } = new();

    public List<RepoOptions> Repos { get; set; } = [];

    public StrykerOptions Stryker { get; set; } = new();

    public HardeningOptions Hardening { get; set; } = new();

    public AgentOptions Agent { get; set; } = new();

    public OutputOptions Output { get; set; } = new();
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

    public string Solution { get; set; } = "";

    public List<TargetOptions> Targets { get; set; } = [];

    public List<string> VerifyTestProjects { get; set; } = [];

    public List<string>? ConventionFiles { get; set; }

    public string? TestNamePattern { get; set; }
}

internal sealed class TargetOptions
{
    public string Project { get; set; } = "";

    public List<string> TestProjects { get; set; } = [];

    public string? TestFilter { get; set; }

    public List<string> Mutate { get; set; } = [];

    public List<string> IgnoreStringMutationsIn { get; set; } = [];
}

internal sealed class StrykerOptions
{
    public static readonly IReadOnlyList<string> DefaultIgnoreMethods =
        ["*Log*", "*Exception.ctor", "ToString", "GetHashCode", "Task.Delay", "*Timeout*"];

    public string Version { get; set; } = "5.0.0";

    public string ToolPath { get; set; } = "~/.cache/test-hardener/tools";

    public string MutationLevel { get; set; } = "Standard";

    public List<string>? IgnoreMethods { get; set; }

    public List<string> IgnoreMutations { get; set; } = [];

    public int? Concurrency { get; set; }

    public int TimeoutMinutesPerTarget { get; set; } = 90;

    public IReadOnlyList<string> EffectiveIgnoreMethods => IgnoreMethods ?? DefaultIgnoreMethods;
}

internal sealed class HardeningOptions
{
    public int MaxGroupsPerRun { get; set; } = 5;

    public int MaxSurvivorsPerGroup { get; set; } = 12;

    public int FixHistoryDays { get; set; } = 180;

    public int MaxRounds { get; set; } = 3;

    public int OriginalRuns { get; set; } = 5;
}

internal sealed class AgentOptions
{
    public string? Model { get; set; }

    public string? ReasoningEffort { get; set; }

    public int MaxMinutes { get; set; } = 30;

    public int MaxToolCalls { get; set; } = 90;

    public int MaxRefusals { get; set; } = 5;

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
