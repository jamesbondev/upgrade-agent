namespace AgentHarness.Copilot;

/// <summary>Built-in Copilot tool names the harness treats specially.</summary>
public static class CopilotToolNames
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

    /// <summary>Sub-agents (they were seen routing around refusals), skills and SQL.</summary>
    public static IReadOnlyList<string> Excluded { get; } = ["task", "read_agent", "list_agents", "write_agent", "skill", "sql"];
}
