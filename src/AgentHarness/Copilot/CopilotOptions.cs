using GitHub.Copilot;

namespace AgentHarness.Copilot;

/// <summary>How to reach GitHub Copilot. The defaults use the local Copilot CLI login (<c>copilot</c>, then <c>/login</c>).</summary>
public sealed class CopilotOptions
{
    /// <summary>A Copilot model ID ("claude-sonnet-4.5", "gpt-5"). Null lets Copilot choose. Your plan decides what is served.</summary>
    public string? Model { get; set; }

    /// <summary>"low", "medium" or "high", for models that support it.</summary>
    public string? ReasoningEffort { get; set; }

    /// <summary>
    /// The environment variable holding a GitHub token, for pipelines. Null or empty uses the local CLI login.
    /// The variable itself is always hidden from the agent.
    /// </summary>
    public string? GitHubTokenEnvironmentVariable { get; set; }

    /// <summary>
    /// Environment variables to hide from the agent, on top of everything named like a secret (see
    /// <see cref="AgentEnvironment.IsSecret"/>). Every command the agent runs inherits what is left.
    /// </summary>
    public IList<string> HiddenEnvironmentVariables { get; } = [];

    /// <summary>Variables to set (or, with a null value, remove) in the agent's environment, e.g. to switch off CLI telemetry.</summary>
    public IDictionary<string, string?> EnvironmentOverrides { get; } = new Dictionary<string, string?>();

    /// <summary>Built-in Copilot tools never offered. The defaults (sub-agents, skills, SQL) widen what can go wrong.</summary>
    public IList<string> ExcludedTools { get; } = [.. CopilotToolNames.Excluded];

    /// <summary>Shown to Copilot as the calling app.</summary>
    public string ClientName { get; set; } = "AgentHarness";

    /// <summary>
    /// A last word on every session's config, after the harness has set it up: MCP servers, extra directories,
    /// anything the harness doesn't expose. Loosening the hardening here is on you.
    /// </summary>
    public Action<SessionConfig>? ConfigureSession { get; set; }
}
