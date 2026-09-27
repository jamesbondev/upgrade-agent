using RepoKit;

namespace TestHardener.Tests.TestSupport;

internal sealed record ProcessCall(string FileName, IReadOnlyList<string> Arguments, string WorkingDirectory, IReadOnlyDictionary<string, string?>? Environment);

internal sealed class FakeProcessRunner(Func<ProcessCall, ProcessResult> respond) : IProcessRunner
{
    public List<ProcessCall> Calls { get; } = [];

    public Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environment = null,
        CancellationToken cancellationToken = default)
    {
        var call = new ProcessCall(fileName, arguments, workingDirectory, environment);
        Calls.Add(call);
        return Task.FromResult(respond(call));
    }

    public static ProcessResult Ok(string output = "") => new(0, output, "");
}
