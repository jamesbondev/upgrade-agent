using UpgradeAgent.Config;
using UpgradeAgent.Run;

namespace UpgradeAgent.Agent;

/// <summary>
/// Counts what one agent session does and decides when it must stop: the tool-call cap, too many refusals,
/// or builds that make no progress. The first stop reason wins; <paramref name="onStop"/> then cancels the session.
/// All state is behind one lock: permission requests and tool events arrive on different threads.
/// </summary>
internal sealed class AgentSessionMeter(AgentOptions options, int initialBuildErrors, Action onStop)
{
    private readonly Lock _lock = new();
    private readonly ProgressMonitor _progress = new(options.MaxRefusalsPerGroup, options.MaxBuildsWithoutProgress, initialBuildErrors);
    private int _modelCalls;
    private int _toolCalls;
    private long _inputTokens;
    private long _outputTokens;
    private double _aiCredits;
    private int _operatorApprovals;
    private int _refusals;
    private string? _servedModel;
    private string? _stopReason;

    /// <summary>Set while the app asks for the structured summary: no tool may run then.</summary>
    public bool SummaryMode { get; set; }

    public string? StopReason
    {
        get
        {
            lock (_lock)
            {
                return _stopReason;
            }
        }
    }

    public void RecordToolCall()
    {
        lock (_lock)
        {
            if (++_toolCalls > options.MaxToolCallsPerGroup)
            {
                Stop($"agent stopped: more than {options.MaxToolCallsPerGroup} tool calls");
            }
        }
    }

    /// <param name="countsTowardStop">False for nudges (such as "read the notes first") that aren't a sign the agent is lost.</param>
    public void RecordRefusal(bool countsTowardStop = true)
    {
        lock (_lock)
        {
            _refusals++;
            if (countsTowardStop && _progress.RecordRefusal() is { } reason)
            {
                Stop(reason);
            }
        }
    }

    public void RecordOperatorApproval()
    {
        lock (_lock)
        {
            _operatorApprovals++;
        }
    }

    public void RecordBuild(string output)
    {
        lock (_lock)
        {
            if (_progress.RecordBuild(output) is { } reason)
            {
                Stop(reason);
            }
        }
    }

    /// <summary>Returns true when the served model changed (the first call, or a provider fallback).</summary>
    public bool RecordUsage(ModelUsage usage)
    {
        lock (_lock)
        {
            _modelCalls++;
            _inputTokens += usage.InputTokens;
            _outputTokens += usage.OutputTokens;
            _aiCredits += usage.AiCredits;
            if (usage.Model is null || usage.Model == _servedModel)
            {
                return false;
            }

            _servedModel = usage.Model;
            return true;
        }
    }

    /// <summary>Stops the session for a reason of the caller's, unless it has already stopped.</summary>
    public void Stop(string reason)
    {
        bool first;
        lock (_lock)
        {
            first = _stopReason is null;
            _stopReason ??= reason;
        }

        if (first)
        {
            onStop();
        }
    }

    public AgentStats Snapshot(TimeSpan duration)
    {
        lock (_lock)
        {
            return new AgentStats(
                _servedModel, _modelCalls, _toolCalls, _inputTokens, _outputTokens, _aiCredits, _operatorApprovals, _refusals, _stopReason is not null, duration);
        }
    }
}
