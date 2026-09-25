using System.Globalization;

namespace UpgradeAgent.Agent.Activities;

/// <summary>The full, untruncated agent log for one group: out/run-*/agent/&lt;group&gt;.log.</summary>
internal sealed class FileActivitySink : IActivitySink, IDisposable
{
    private readonly StreamWriter _writer;
    private readonly TimeProvider _time;

    public FileActivitySink(string path, TimeProvider time)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(path, append: false) { AutoFlush = true };
        _time = time;
    }

    public void Write(ActivityEvent activity)
    {
        var line = activity switch
        {
            Note note => note.Text,
            AgentMessage message => $"AGENT {message.Text}",
            ToolStarted tool => $"TOOL {tool.Tool} {tool.Detail}",
            ToolFailed failed => $"  FAILED {failed.Error}",
            ActionRefused refused => $"REFUSED {refused.Action}: {refused.Reason}",
            BuildChecked build => $"  BUILD {build.Errors} errors {build.TopCodes}".TrimEnd(),
            TestsChecked tests => $"  TESTS {tests.Passed} passed, {tests.Failed} failed",
            Transcript transcript => $"{transcript.Label}\n{transcript.Text}",
            _ => activity.ToString(),
        };
        _writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{_time.GetUtcNow():HH:mm:ss} {line}"));
    }

    public void Dispose() => _writer.Dispose();
}
