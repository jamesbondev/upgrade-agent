using AgentHarness.Policies;

namespace AgentHarness;

/// <summary>
/// The front door: starts sessions on a backend, with a prompter for the actions your policy leaves to a human.
/// <code>
/// await using var copilot = new CopilotBackend(new CopilotOptions { Model = "claude-sonnet-4.5" });
/// var runner = new AgentRunner(copilot, ApprovalPrompter.Console);
///
/// await using var session = await runner.StartAsync(new AgentSessionOptions
/// {
///     WorkingDirectory = repo,
///     Instructions = "You fix failing builds. Verify with dotnet build.",
///     Policy = new WorkspacePolicy(repo, o => o.Commands["dotnet"] = CommandRules.ApproveVerbs("build", "test")),
/// }, ct);
///
/// var reply = await session.SendAsync("Make the build pass.", ct);
/// </code>
/// One runner can serve many sessions, one after another or at once. It doesn't own the backend: dispose that yourself.
/// </summary>
/// <param name="prompter">Asked about <see cref="ToolVerdict.Ask"/> decisions. Default: <see cref="ApprovalPrompter.DeclineAll"/>.</param>
public sealed class AgentRunner(IAgentBackend backend, IApprovalPrompter? prompter = null, TimeProvider? time = null)
{
    public IAgentBackend Backend => backend;

    /// <summary>Starts a session. Its time budget starts now. Dispose it when done.</summary>
    public Task<AgentSession> StartAsync(AgentSessionOptions options, CancellationToken cancellationToken = default) =>
        AgentSession.StartAsync(backend, options, prompter ?? ApprovalPrompter.DeclineAll, time ?? TimeProvider.System, cancellationToken);

    /// <summary>One turn in a fresh session: start, send, dispose.</summary>
    public async Task<AgentResult> RunAsync(AgentSessionOptions options, string message, CancellationToken cancellationToken = default)
    {
        await using var session = await StartAsync(options, cancellationToken);
        var reply = await session.SendAsync(message, cancellationToken);
        return new AgentResult(reply, session.Stats);
    }

    /// <summary>One turn, then a structured reply (<see cref="AgentSession.AskAsync{T}"/>) when the turn wasn't stopped.</summary>
    public async Task<AgentResult<T>> RunAsync<T>(AgentSessionOptions options, string message, string question, CancellationToken cancellationToken = default)
        where T : class
    {
        await using var session = await StartAsync(options, cancellationToken);
        var reply = await session.SendAsync(message, cancellationToken);
        var structured = reply.Stopped ? null : await session.AskAsync<T>(question, cancellationToken: cancellationToken);
        return new AgentResult<T>(reply, structured, session.Stats);
    }
}

public sealed record AgentResult(AgentReply Reply, AgentStats Stats);

/// <param name="Structured">Null when the turn was stopped before it could be asked.</param>
public sealed record AgentResult<T>(AgentReply Reply, StructuredReply<T>? Structured, AgentStats Stats)
    where T : class;
