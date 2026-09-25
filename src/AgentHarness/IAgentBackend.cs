namespace AgentHarness;

public interface IAgentBackend : IAsyncDisposable
{
    string Name { get; }

    string? Model { get; }

    Task<IAgentBackendSession> StartSessionAsync(AgentBackendSettings settings, CancellationToken cancellationToken);
}

public interface IAgentBackendSession : IAsyncDisposable
{
    Task<string?> SendAsync(string message, CancellationToken cancellationToken);
}

public sealed record AgentBackendSettings(
    string WorkingDirectory,
    string Instructions,
    IReadOnlyList<AgentTool> Tools,
    bool UseBuiltInTools,
    bool AllowWebFetch,
    Func<ToolRequest, CancellationToken, Task<ToolApproval>> AuthorizeAsync,
    Action<AgentEvent> OnEvent);

public sealed record ToolApproval(bool Allowed, string? Feedback)
{
    public static ToolApproval Allow { get; } = new(true, null);

    public static ToolApproval Deny(string feedback) => new(false, feedback);
}
