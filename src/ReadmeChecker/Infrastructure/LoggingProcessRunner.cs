using Microsoft.Extensions.Logging;
using RepoKit;

namespace ReadmeChecker.Infrastructure;

internal sealed partial class LoggingProcessRunner(IProcessRunner inner, ILogger<LoggingProcessRunner> logger, TimeProvider time) : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment = null,
        CancellationToken cancellationToken = default)
    {
        var started = time.GetTimestamp();
        var result = await inner.RunAsync(fileName, arguments, workingDirectory, environment, cancellationToken);
        var elapsed = time.GetElapsedTime(started);
        LogCompleted(fileName, arguments, workingDirectory, result.ExitCode, elapsed);
        return result;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "{FileName} [{Arguments}] (in {WorkingDirectory}) exited {ExitCode} after {Elapsed}")]
    private partial void LogCompleted(string fileName, IReadOnlyList<string> arguments, string workingDirectory, int exitCode, TimeSpan elapsed);
}
