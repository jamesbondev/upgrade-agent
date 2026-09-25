using Microsoft.Extensions.Time.Testing;

namespace AgentHarness.Tests;

/// <summary>The time budget behind <see cref="AgentLimits.MaxDuration"/>. Time is faked, so nothing here waits.</summary>
public class PausableTimeoutTests
{
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(10);

    private readonly FakeTimeProvider _time = new();

    [Fact]
    public void FiresWhenTheBudgetIsUsedUp()
    {
        using var timeout = new PausableTimeout(Budget, _time);

        _time.Advance(Budget - TimeSpan.FromSeconds(1));
        Assert.False(timeout.Token.IsCancellationRequested);

        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(timeout.Token.IsCancellationRequested);
    }

    [Fact]
    public void PausingStopsTheClock()
    {
        using var timeout = new PausableTimeout(Budget, _time);

        using (timeout.Pause())
        {
            _time.Advance(TimeSpan.FromHours(1));
        }

        Assert.False(timeout.Token.IsCancellationRequested);
    }

    [Fact]
    public void ResumingContinuesWithWhatWasLeft()
    {
        using var timeout = new PausableTimeout(Budget, _time);
        _time.Advance(TimeSpan.FromMinutes(4));

        using (timeout.Pause())
        {
            _time.Advance(TimeSpan.FromHours(1));
        }

        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.False(timeout.Token.IsCancellationRequested);

        _time.Advance(TimeSpan.FromMinutes(1));
        Assert.True(timeout.Token.IsCancellationRequested);
    }

    [Fact]
    public void NestedPausesResumeOnlyWhenTheLastOneEnds()
    {
        using var timeout = new PausableTimeout(Budget, _time);
        var outer = timeout.Pause();
        var inner = timeout.Pause();

        inner.Dispose();
        _time.Advance(TimeSpan.FromHours(1));
        Assert.False(timeout.Token.IsCancellationRequested);

        outer.Dispose();
        _time.Advance(Budget);
        Assert.True(timeout.Token.IsCancellationRequested);
    }

    [Fact]
    public void DisposingAPauseTwiceResumesOnlyOnce()
    {
        using var timeout = new PausableTimeout(Budget, _time);
        var first = timeout.Pause();
        first.Dispose();
        first.Dispose();

        // Had the second dispose counted, this pause would be ignored and the budget would run out.
        using (timeout.Pause())
        {
            _time.Advance(TimeSpan.FromHours(1));
            Assert.False(timeout.Token.IsCancellationRequested);
        }
    }

    [Fact]
    public void ResumingAfterTheTimeoutWasDisposedIsHarmless()
    {
        var timeout = new PausableTimeout(Budget, _time);
        var pause = timeout.Pause();
        timeout.Dispose();

        var exception = Record.Exception(pause.Dispose);

        Assert.Null(exception);
    }

    [Fact]
    public void PausingAfterTheTimeoutWasDisposedIsHarmless()
    {
        var timeout = new PausableTimeout(Budget, _time);
        timeout.Dispose();

        var exception = Record.Exception(() => timeout.Pause().Dispose());

        Assert.Null(exception);
    }

    [Fact]
    public void ANullBudgetNeverFires()
    {
        using var timeout = new PausableTimeout(null, _time);

        using (timeout.Pause())
        {
            _time.Advance(TimeSpan.FromDays(1));
        }

        _time.Advance(TimeSpan.FromDays(365));
        Assert.False(timeout.Token.IsCancellationRequested);
    }
}
