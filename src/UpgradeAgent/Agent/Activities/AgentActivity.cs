namespace UpgradeAgent.Agent.Activities;

internal sealed class AgentActivity : IActivitySink
{
    private readonly Lock _lock = new();
    private readonly List<IActivitySink> _sinks;

    public AgentActivity(params IEnumerable<IActivitySink> sinks) => _sinks = [.. sinks];

    public void Write(ActivityEvent activity)
    {
        lock (_lock)
        {
            foreach (var sink in _sinks)
            {
                sink.Write(activity);
            }
        }
    }

    public IDisposable Attach(IActivitySink sink)
    {
        lock (_lock)
        {
            _sinks.Add(sink);
        }

        return new Detach(this, sink);
    }

    private sealed class Detach(AgentActivity owner, IActivitySink sink) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._lock)
            {
                owner._sinks.Remove(sink);
            }
        }
    }
}
