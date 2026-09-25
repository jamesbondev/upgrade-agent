namespace AgentHarness.Tests.TestSupport;

internal sealed class EventRecorder : IAgentObserver
{
    private readonly List<AgentEvent> _events = [];

    public IReadOnlyList<AgentEvent> Events
    {
        get
        {
            lock (_events)
            {
                return [.. _events];
            }
        }
    }

    public IReadOnlyList<T> OfType<T>()
        where T : AgentEvent => [.. Events.OfType<T>()];

    public void OnEvent(AgentEvent agentEvent)
    {
        lock (_events)
        {
            _events.Add(agentEvent);
        }
    }
}
