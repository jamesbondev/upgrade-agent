namespace AgentHarness;

/// <summary>
/// A model provider with an agent runtime (tool loop, file and shell tools). Everything that doesn't depend on
/// the provider (permissions, budgets, stop rules, structured replies) lives in <see cref="AgentRunner"/>; a
/// backend only translates between its SDK and the neutral types below. <see cref="Copilot.CopilotBackend"/> is
/// the real one and <see cref="Testing.ScriptedBackend"/> the fake for tests; another provider is one more class.
/// </summary>
public interface IAgentBackend : IAsyncDisposable
{
    /// <summary>Shown in logs and telemetry, e.g. "GitHub Copilot".</summary>
    string Name { get; }

    /// <summary>The model asked for, or null when the provider chooses. The model actually served is reported in <see cref="ModelUsage"/>.</summary>
    string? Model { get; }

    Task<IAgentBackendSession> StartSessionAsync(AgentBackendSettings settings, CancellationToken cancellationToken);
}

/// <summary>One conversation with the provider. <see cref="AgentSession"/> wraps it with the harness's rules.</summary>
public interface IAgentBackendSession : IAsyncDisposable
{
    /// <summary>Sends a user turn and waits for the agent to finish it; returns the final reply text.</summary>
    Task<string?> SendAsync(string message, CancellationToken cancellationToken);
}

/// <param name="Instructions">Appended to the provider's own system prompt.</param>
/// <param name="Tools">The app's own tools, offered alongside (or, without <paramref name="UseBuiltInTools"/>, instead of) the runtime's.</param>
/// <param name="UseBuiltInTools">False offers only <paramref name="Tools"/>: no shell, no file tools.</param>
/// <param name="AllowWebFetch">Offer the runtime's web fetch tool. Fetched pages are untrusted input.</param>
/// <param name="AuthorizeAsync">Called before every tool action; the backend must not act without an approval.</param>
/// <param name="OnEvent">Tool calls, messages and usage, as they happen (on background threads).</param>
public sealed record AgentBackendSettings(
    string WorkingDirectory,
    string Instructions,
    IReadOnlyList<AgentTool> Tools,
    bool UseBuiltInTools,
    bool AllowWebFetch,
    Func<ToolRequest, CancellationToken, Task<ToolApproval>> AuthorizeAsync,
    Action<AgentEvent> OnEvent);

/// <param name="Feedback">For a denial, sent back to the model so it can choose another way.</param>
public sealed record ToolApproval(bool Allowed, string? Feedback)
{
    public static ToolApproval Allow { get; } = new(true, null);

    public static ToolApproval Deny(string feedback) => new(false, feedback);
}
