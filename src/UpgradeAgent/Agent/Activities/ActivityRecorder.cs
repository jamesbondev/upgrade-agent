namespace UpgradeAgent.Agent.Activities;

/// <summary>Keeps every event with its timing, for a recording.</summary>
internal sealed class ActivityRecorder(TimeProvider time) : IActivitySink
{
    private readonly long _started = time.GetTimestamp();
    private readonly List<RecordedActivity> _events = [];

    public IReadOnlyList<RecordedActivity> Events => _events;

    public void Write(ActivityEvent activity) =>
        _events.Add(new RecordedActivity(time.GetElapsedTime(_started).TotalSeconds, activity));
}
