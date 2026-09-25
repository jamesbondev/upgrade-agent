namespace UpgradeAgent.Agent;

/// <summary>
/// A time budget that stops counting while paused, so time the operator spends at an approval prompt
/// doesn't use up the agent's budget.
/// </summary>
internal sealed class PausableTimeout : IDisposable
{
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _source;
    private readonly TimeProvider _time;
    private TimeSpan _remaining;
    private long _runningSince;
    private int _pauses;

    public PausableTimeout(TimeSpan budget, TimeProvider time)
    {
        _time = time;
        _remaining = budget;
        _runningSince = time.GetTimestamp();
        _source = new CancellationTokenSource(budget, time);
    }

    public CancellationToken Token => _source.Token;

    public IDisposable Pause()
    {
        lock (_lock)
        {
            if (_pauses++ == 0)
            {
                _remaining -= _time.GetElapsedTime(_runningSince);
                _source.CancelAfter(Timeout.InfiniteTimeSpan);
            }
        }

        return new Resumer(this);
    }

    public void Dispose() => _source.Dispose();

    private void Resume()
    {
        lock (_lock)
        {
            if (--_pauses == 0)
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
