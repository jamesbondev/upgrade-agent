namespace UpgradeAgent.Agent.Activities;

internal interface IActivitySink
{
    void Write(ActivityEvent activity);
}
