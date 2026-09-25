using AgentHarness.Policies;

namespace AgentHarness;

public sealed class AgentRunner(IAgentBackend backend, IApprovalPrompter? prompter = null, TimeProvider? time = null)
{
    public IAgentBackend Backend => backend;

    public Task<AgentSession> StartAsync(AgentSessionOptions options, CancellationToken cancellationToken = default) =>
        AgentSession.StartAsync(backend, options, prompter ?? ApprovalPrompter.DeclineAll, time ?? TimeProvider.System, cancellationToken);

    public async Task<AgentResult> RunAsync(AgentSessionOptions options, string message, CancellationToken cancellationToken = default)
    {
        await using var session = await StartAsync(options, cancellationToken);
        var reply = await session.SendAsync(message, cancellationToken);
        return new AgentResult(reply, session.Stats);
    }

    public async Task<AgentResult<T>> RunAsync<T>(AgentSessionOptions options, string message, string question, CancellationToken cancellationToken = default)
        where T : class
    {
        await using var session = await StartAsync(options, cancellationToken);
        var reply = await session.SendAsync(message, cancellationToken);
        var structured = reply.Stopped ? null : await session.AskAsync<T>(question, cancellationToken);
        return new AgentResult<T>(reply, structured, session.Stats);
    }
}

public sealed record AgentResult(AgentReply Reply, AgentStats Stats);

public sealed record AgentResult<T>(AgentReply Reply, StructuredReply<T>? Structured, AgentStats Stats)
    where T : class;
