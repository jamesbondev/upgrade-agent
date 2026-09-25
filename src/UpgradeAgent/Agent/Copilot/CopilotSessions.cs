using GitHub.Copilot;
using UpgradeAgent.Config;

namespace UpgradeAgent.Agent.Copilot;

/// <summary>The one place Copilot sessions are configured, so every session gets the same hardening.</summary>
internal static class CopilotSessions
{
    public const string ClientName = "UpgradeAgent";

    /// <summary>
    /// Repo-driven behaviour is off: the target repository must not be able to change the agent's config,
    /// hooks, skills or instructions, and git stays the app's job.
    /// </summary>
    public static SessionConfig Hardened(AgentOptions options, string worktree) => new()
    {
        ClientName = ClientName,
        Model = options.Model,
        ReasoningEffort = options.ReasoningEffort,
        WorkingDirectory = worktree,
        EnableConfigDiscovery = false,
        EnableFileHooks = false,
        EnableSkills = false,
        SkipCustomInstructions = true,
        EnableOnDemandInstructionDiscovery = false,
        EnableHostGitOperations = false,
    };
}

/// <summary>Built-in Copilot tool names the app treats specially.</summary>
internal static class CopilotToolNames
{
    public const string Bash = "bash";
    public const string PowerShell = "powershell";
    public const string View = "view";
    public const string Create = "create";
    public const string Edit = "edit";
    public const string Grep = "grep";
    public const string Glob = "glob";
    public const string ReadBash = "read_bash";
    public const string ListBash = "list_bash";
    public const string WebFetch = "web_fetch";

    /// <summary>Sub-agents, skills and SQL add nothing to fixing call sites and widen what can go wrong.</summary>
    public static readonly IReadOnlyList<string> Excluded = ["task", "read_agent", "list_agents", "write_agent", "skill", "sql"];
}
