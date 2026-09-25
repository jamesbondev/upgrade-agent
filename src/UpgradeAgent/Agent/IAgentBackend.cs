using UpgradeAgent.Agent.Activities;

namespace UpgradeAgent.Agent;

/// <summary>
/// A model provider with an agent runtime (tool loop, file and shell tools). Everything that doesn't depend on
/// the provider (prompts, permissions, budgets, the structured summary) lives in <see cref="AgentFixRunner"/>;
/// a backend only translates between its SDK and the neutral types below.
/// </summary>
internal interface IAgentBackend : IAsyncDisposable
{
    /// <summary>Shown in the run output, e.g. "GitHub Copilot".</summary>
    string Name { get; }

    Task<IAgentSession> StartSessionAsync(AgentSessionSettings settings, CancellationToken cancellationToken);
}

internal interface IAgentSession : IAsyncDisposable
{
    /// <summary>Sends a user turn and waits for the agent to finish it; returns the final reply text.</summary>
    Task<string?> SendAsync(string message, CancellationToken cancellationToken);
}

/// <param name="AuthorizeAsync">Called before every tool action; the backend must not act without an approval.</param>
/// <param name="OnEvent">Tool calls, messages and usage, as they happen (on background threads).</param>
internal sealed record AgentSessionSettings(
    string WorkingDirectory,
    string SystemPrompt,
    bool AllowWebFetch,
    Func<ToolRequest, CancellationToken, Task<ToolPermission>> AuthorizeAsync,
    Action<AgentEvent> OnEvent);

/// <summary>An action the agent wants to take, described without SDK types.</summary>
internal abstract record ToolRequest;

/// <param name="PossiblePaths">Paths the runtime's own parser found in the command, if it reports them.</param>
internal sealed record ShellRequest(string CommandLine, bool WritesFile, IReadOnlyList<string> PossiblePaths) : ToolRequest;

internal sealed record FileWriteRequest(string Path) : ToolRequest;

internal sealed record FileReadRequest(string Path) : ToolRequest;

internal sealed record WebFetchRequest(string Url) : ToolRequest;

/// <summary>Anything else the runtime offers (MCP, memory, custom tools…).</summary>
internal sealed record OtherToolRequest(string Kind) : ToolRequest;

/// <param name="Feedback">For a denial, sent back to the model so it can choose another way.</param>
internal sealed record ToolPermission(bool Allowed, string? Feedback)
{
    public static ToolPermission Allow { get; } = new(true, null);

    public static ToolPermission Deny(string feedback) => new(false, feedback);
}

internal abstract record AgentEvent;

/// <param name="Detail">The command, path or pattern, as given by the model.</param>
/// <param name="RawArguments">All of the call's arguments as text, whatever the tool (used to notice which files it read).</param>
internal sealed record ToolCallStarted(string CallId, ToolKind Kind, string Tool, string Detail, string? RawArguments = null) : AgentEvent;

internal sealed record ToolCallCompleted(string CallId, bool Success, string? Output, string? Error) : AgentEvent;

internal sealed record AssistantMessage(string Text) : AgentEvent;

/// <param name="AiCredits">Provider billing units for the call, where the provider reports them.</param>
internal sealed record ModelUsage(string? Model, long InputTokens, long OutputTokens, double AiCredits) : AgentEvent;
