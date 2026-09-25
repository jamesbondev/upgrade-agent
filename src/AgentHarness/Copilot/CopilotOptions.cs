using GitHub.Copilot;

namespace AgentHarness.Copilot;

public sealed class CopilotOptions
{
    public string? Model { get; set; }

    public string? ReasoningEffort { get; set; }

    public string? GitHubTokenEnvironmentVariable { get; set; }

    public IList<string> HiddenEnvironmentVariables { get; } = [];

    public IDictionary<string, string?> EnvironmentOverrides { get; } = new Dictionary<string, string?>();

    public IList<string> ExcludedTools { get; } = [.. CopilotToolNames.Excluded];

    public string ClientName { get; set; } = "AgentHarness";

    public Action<SessionConfig>? ConfigureSession { get; set; }
}
