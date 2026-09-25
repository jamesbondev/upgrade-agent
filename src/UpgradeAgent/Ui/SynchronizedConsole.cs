using Spectre.Console;

namespace UpgradeAgent.Ui;

/// <summary>
/// The console shared by the main flow and agent events, which arrive on background threads. Writes and
/// prompts take turns, so an approval prompt is never interleaved with agent output.
/// </summary>
internal sealed class SynchronizedConsole(IAnsiConsole console) : IDisposable
{
    private readonly SemaphoreSlim _turn = new(1, 1);

    public void Write(Action<IAnsiConsole> write)
    {
        _turn.Wait();
        try
        {
            write(console);
        }
        finally
        {
            _turn.Release();
        }
    }

    public void Dispose() => _turn.Dispose();

    /// <summary>Runs an interactive exchange (such as a prompt) with the console to itself.</summary>
    public async Task<T> ExclusiveAsync<T>(Func<IAnsiConsole, Task<T>> exchange, CancellationToken cancellationToken)
    {
        await _turn.WaitAsync(cancellationToken);
        try
        {
            return await exchange(console);
        }
        finally
        {
            _turn.Release();
        }
    }
}
