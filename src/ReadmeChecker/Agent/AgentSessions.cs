using AgentHarness;
using AgentHarness.Policies;

namespace ReadmeChecker.Agent;

internal sealed record SessionResult<T>(AgentReply? Reply, StructuredReply<T>? Structured, AgentStats? Stats, string? Failure)
    where T : class;

internal static class AgentSessions
{
    public static async Task<SessionResult<T>> ExploreThenAskAsync<T>(
        IAgentBackend backend, AgentSessionOptions options, string task, string question, bool askAfterStop, TimeProvider time, CancellationToken cancellationToken)
        where T : class
    {
        AgentSession? session = null;
        try
        {
            session = await new AgentRunner(backend, ApprovalPrompter.DeclineAll, time).StartAsync(options, cancellationToken);
            var reply = await session.SendAsync(task, cancellationToken);
            if (reply.Stopped && !askAfterStop)
            {
                return new SessionResult<T>(reply, null, session.Stats, null);
            }

            var structured = await session.AskAsync<T>(question, cancellationToken);
            return new SessionResult<T>(reply, structured, session.Stats, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new SessionResult<T>(null, null, session?.Stats, $"the agent failed: {ex.GetBaseException().Message}");
        }
        finally
        {
            if (session is not null)
            {
                await session.DisposeAsync();
            }
        }
    }

    public static void ThrowIfQuotaExceeded<T>(SessionResult<T> result)
        where T : class
    {
        foreach (var text in new[] { result.Failure, result.Structured?.Error, result.Reply?.StopReason })
        {
            if (text is not null && (text.Contains("exceeded your monthly quota", StringComparison.OrdinalIgnoreCase)
                || text.Contains("quota exceeded", StringComparison.OrdinalIgnoreCase)))
            {
                throw new AgentUnavailableException($"Copilot's usage quota is used up, so the run stopped: {text}");
            }
        }
    }

    public static AgentStats? Sum(IReadOnlyList<AgentStats> all)
    {
        if (all.Count == 0)
        {
            return null;
        }

        var stopped = all.Where(s => s.StopReason is not null).ToList();
        return new AgentStats(
            string.Join(", ", all.Select(s => s.Model).OfType<string>().Distinct(StringComparer.Ordinal)) is { Length: > 0 } models ? models : null,
            all.Sum(s => s.ModelCalls),
            all.Sum(s => s.ToolCalls),
            all.Sum(s => s.InputTokens),
            all.Sum(s => s.OutputTokens),
            all.Sum(s => s.AiCredits),
            all.Sum(s => s.OperatorApprovals),
            all.Sum(s => s.Refusals),
            stopped.Count == 0 ? null : $"{stopped.Count} of {all.Count} sessions stopped: {stopped[0].StopReason}",
            TimeSpan.FromTicks(all.Sum(s => s.Duration.Ticks)));
    }
}

internal sealed class AgentLog : IAsyncDisposable
{
    private readonly StreamWriter _writer;
    private readonly Lock _lock = new();

    private AgentLog(StreamWriter writer, TimeProvider time)
    {
        _writer = writer;
        Observer = AgentObserver.From(e =>
        {
            lock (_lock)
            {
                _writer.WriteLine($"{time.GetLocalNow():HH:mm:ss.fff} {e}");
            }
        });
    }

    public IAgentObserver Observer { get; }

    public static AgentLog Open(string path, TimeProvider time)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new AgentLog(new StreamWriter(path, append: true) { AutoFlush = true }, time);
    }

    public void Note(string text)
    {
        lock (_lock)
        {
            _writer.WriteLine(text);
        }
    }

    public ValueTask DisposeAsync() => _writer.DisposeAsync();
}
