namespace AgentHarness;

public sealed class FileAgentLog : IAsyncDisposable
{
    private readonly StreamWriter _writer;
    private readonly Lock _lock = new();

    private FileAgentLog(StreamWriter writer, TimeProvider time)
    {
        _writer = writer;
        Observer = AgentObserver.From(e =>
        {
            lock (_lock)
            {
                _writer.WriteLine($"{time.GetLocalNow():HH:mm:ss.fff} {e}");
            }
        });
    }

    public IAgentObserver Observer { get; }

    public static FileAgentLog Open(string path, TimeProvider? time = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        return new FileAgentLog(new StreamWriter(path, append: true) { AutoFlush = true }, time ?? TimeProvider.System);
    }

    public void Note(string text)
    {
        lock (_lock)
        {
            _writer.WriteLine(text);
        }
    }

    public ValueTask DisposeAsync() => _writer.DisposeAsync();
}
