using AgentHarness;
using AgentHarness.Policies;
using ReadmeChecker.Agent;
using ReadmeChecker.Config;
using ReadmeChecker.Detection;

namespace ReadmeChecker.Fixing;

internal sealed record FixAttempt(string? Summary, AgentStats? Stats, string? Failure);

internal interface IReadmeFixer
{
    Task<FixAttempt> FixAsync(
        string repoName, string root, RepoFacts facts, IReadOnlyList<ReadmeIssue> issues, IReadOnlyList<Signal> certain, string logPath, CancellationToken cancellationToken);
}

internal sealed class ReadmeFixer(IAgentBackendFactory backends, AgentOptions options, TimeProvider time) : IReadmeFixer
{
    public const string ReadOnlyRefusal = "Only reading, read-only commands and editing the README are available in this session.";

    public async Task<FixAttempt> FixAsync(
        string repoName, string root, RepoFacts facts, IReadOnlyList<ReadmeIssue> issues, IReadOnlyList<Signal> certain, string logPath, CancellationToken cancellationToken)
    {
        var readme = facts.Readme!;
        await using var backend = backends.Create();
        await backends.EnsureReadyAsync(backend, root, cancellationToken);

        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        await using var log = new StreamWriter(logPath, append: true) { AutoFlush = true };
        var logLock = new Lock();
        var sessionOptions = new AgentSessionOptions
        {
            Name = $"readme fix {repoName}",
            WorkingDirectory = root,
            Instructions = FixPrompts.System(readme.Path),
            Policy = WritePolicy(root, readme.Path),
            Limits = new AgentLimits
            {
                MaxDuration = TimeSpan.FromMinutes(options.MaxMinutes),
                MaxToolCalls = options.MaxToolCalls,
                MaxRefusals = options.MaxRefusals,
            },
            Observers =
            [
                AgentObserver.From(e =>
                {
                    lock (logLock)
                    {
                        log.WriteLine($"{time.GetLocalNow():HH:mm:ss.fff} {e}");
                    }
                }),
            ],
        };

        try
        {
            var result = await new AgentRunner(backend, ApprovalPrompter.DeclineAll, time).RunAsync(
                sessionOptions, FixPrompts.Task(repoName, readme, issues, certain), cancellationToken);
            return result.Reply.Stopped
                ? new FixAttempt(null, result.Stats, $"the agent was stopped: {result.Reply.StopReason}")
                : new FixAttempt(result.Reply.Text, result.Stats, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new FixAttempt(null, null, $"the agent failed: {ex.GetBaseException().Message}");
        }
    }

    internal static IToolPolicy WritePolicy(string root, string readmePath)
    {
        var workspace = new WorkspacePolicy(root);
        var readme = Path.GetFullPath(Path.Combine(root, readmePath));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return ToolPolicy.From(request => request switch
        {
            FileReadRequest read => workspace.EvaluateRead(read.Path),
            ShellRequest shell => workspace.EvaluateShell(shell.CommandLine, shell.WritesFile, shell.PossiblePaths),
            FileWriteRequest write when Path.GetFullPath(write.Path, root).Equals(readme, comparison) => ToolDecision.Approve("the README"),
            FileWriteRequest => ToolDecision.Reject($"Only {readmePath} may be changed in this session. Leave every other file as it is."),
            _ => ToolDecision.Reject(ReadOnlyRefusal),
        });
    }
}
