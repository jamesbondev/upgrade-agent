using Spectre.Console;

namespace UpgradeAgent.Ui;

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
