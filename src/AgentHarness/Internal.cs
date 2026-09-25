using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace AgentHarness;

public static class AgentTelemetry
{
    public const string SourceName = "AgentHarness";

    internal static readonly ActivitySource Source = new(SourceName);

    private static readonly Meter Meter = new(SourceName);

    private static readonly Counter<long> Tokens = Meter.CreateCounter<long>("gen_ai.client.token.usage", "{token}", "Tokens used by agents.");

    private static readonly Counter<long> ToolCalls = Meter.CreateCounter<long>("agent_harness.tool_calls", "{call}", "Tool calls agents made.");

    internal static Activity? StartSession(string provider, string session, string? model) =>
        Source.StartActivity($"invoke_agent {session}", ActivityKind.Client)?
            .SetTag("gen_ai.operation.name", "invoke_agent")
            .SetTag("gen_ai.provider.name", provider)
            .SetTag("gen_ai.request.model", model)
            .SetTag("agent_harness.session", session);

    internal static void RecordToolCall(string tool) => ToolCalls.Add(1, new KeyValuePair<string, object?>("gen_ai.tool.name", tool));

    internal static void RecordUsage(ModelUsage usage)
    {
        KeyValuePair<string, object?> model = new("gen_ai.response.model", usage.Model);
        Tokens.Add(usage.InputTokens, model, new("gen_ai.token.type", "input"));
        Tokens.Add(usage.OutputTokens, model, new("gen_ai.token.type", "output"));
    }
}

internal sealed class PausableTimeout : IDisposable
{
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _source;
    private readonly TimeProvider _time;
    private readonly bool _unlimited;
    private TimeSpan _remaining;
    private long _runningSince;
    private int _pauses;
    private bool _disposed;

    public PausableTimeout(TimeSpan? budget, TimeProvider time)
    {
        _time = time;
        _unlimited = budget is null;
        _remaining = budget ?? Timeout.InfiniteTimeSpan;
        _runningSince = time.GetTimestamp();
        _source = new CancellationTokenSource(_remaining, time);
    }

    public CancellationToken Token => _source.Token;

    public IDisposable Pause()
    {
        lock (_lock)
        {
            if (_pauses++ == 0 && !_disposed && !_unlimited)
            {
                _remaining -= _time.GetElapsedTime(_runningSince);
                _source.CancelAfter(Timeout.InfiniteTimeSpan);
            }
        }

        return new Resumer(this);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _source.Dispose();
        }
    }

    private void Resume()
    {
        lock (_lock)
        {
            if (--_pauses == 0 && !_disposed && !_unlimited)
            {
                _runningSince = _time.GetTimestamp();
                _source.CancelAfter(_remaining > TimeSpan.Zero ? _remaining : TimeSpan.Zero);
            }
        }
    }

    private sealed class Resumer(PausableTimeout owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Resume();
            }
        }
    }
}

internal static class PathText
{
    public static string Relative(string root, string text)
    {
        if (root.Length == 0)
        {
            return text;
        }

        var trimmed = Path.TrimEndingDirectorySeparator(root);
        return text
            .Replace(trimmed + Path.DirectorySeparatorChar, "", StringComparison.Ordinal)
            .Replace(trimmed, ".", StringComparison.Ordinal);
    }
}
