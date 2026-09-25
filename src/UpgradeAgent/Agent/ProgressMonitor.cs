using UpgradeAgent.Build;

namespace UpgradeAgent.Agent;

/// <summary>
/// Stops an agent that is flailing rather than waiting for the tool-call budget: too many refused actions,
/// or several builds in a row that don't beat the lowest error count so far. A green build resets the count.
/// Limits of 0 disable a check. Not thread-safe on its own: <see cref="AgentSessionMeter"/> calls it under its lock.
/// </summary>
internal sealed class ProgressMonitor(int maxRefusals, int maxBuildsWithoutProgress, int initialErrors)
{
    private int _refusals;
    private int? _lowestErrors = initialErrors > 0 ? initialErrors : null;
    private int _buildsWithoutProgress;

    /// <summary>Returns a stop reason once the refusal limit is exceeded, otherwise null.</summary>
    public string? RecordRefusal()
    {
        return ++_refusals > maxRefusals && maxRefusals > 0
            ? $"agent stopped: more than {maxRefusals} refused actions"
            : null;
    }

    /// <summary>Records one build's output. Output with no recognisable result (e.g. cut by <c>| head</c>) is ignored.</summary>
    public string? RecordBuild(string output)
    {
        if (BuildOutputParser.CountErrors(output) is not { } errors)
        {
            return null;
        }

        if (errors == 0)
        {
            _lowestErrors = null;
            _buildsWithoutProgress = 0;
            return null;
        }

        if (_lowestErrors is null || errors < _lowestErrors)
        {
            _lowestErrors = errors;
            _buildsWithoutProgress = 0;
            return null;
        }

        return ++_buildsWithoutProgress >= maxBuildsWithoutProgress && maxBuildsWithoutProgress > 0
            ? $"agent stopped: {_buildsWithoutProgress} builds without reducing the errors below {_lowestErrors}"
            : null;
    }
}
