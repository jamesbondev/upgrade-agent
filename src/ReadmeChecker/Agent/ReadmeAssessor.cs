using AgentHarness;
using AgentHarness.Policies;
using ReadmeChecker.Config;
using ReadmeChecker.Detection;

namespace ReadmeChecker.Agent;

internal sealed record AssessmentOutcome(
    AssessedVerdict? Verdict,
    IReadOnlyList<ReadmeIssue> Issues,
    IReadOnlyList<RejectedIssue> Rejected,
    string? Summary,
    AgentStats? Stats,
    string? Failure)
{
    public static AssessmentOutcome Failed(string failure, AgentStats? stats) => new(null, [], [], null, stats, failure);
}

internal interface IReadmeAssessor
{
    Task<AssessmentOutcome> AssessAsync(string repoName, string root, RepoFacts facts, SignalScan scan, string logPath, CancellationToken cancellationToken);
}

internal sealed class ReadmeAssessor(IAgentBackendFactory backends, AgentOptions options, TimeProvider time) : IReadmeAssessor
{
    public const string ReadOnlyRefusal = "This session is read-only: read files and run read-only commands, then answer. Don't change anything.";

    public async Task<AssessmentOutcome> AssessAsync(
        string repoName, string root, RepoFacts facts, SignalScan scan, string logPath, CancellationToken cancellationToken)
    {
        await using var backend = backends.Create();
        await backends.EnsureReadyAsync(backend, root, cancellationToken);

        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        await using var log = new StreamWriter(logPath) { AutoFlush = true };
        var logLock = new Lock();
        var sessionOptions = new AgentSessionOptions
        {
            Name = $"readme {repoName}",
            WorkingDirectory = root,
            Instructions = AssessmentPrompts.System,
            Policy = ReadOnlyPolicy(root),
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
            var result = await new AgentRunner(backend, ApprovalPrompter.DeclineAll, time).RunAsync<ReadmeAssessment>(
                sessionOptions, AssessmentPrompts.Task(repoName, facts, scan), AssessmentPrompts.Question, cancellationToken);

            if (result.Reply.Stopped)
            {
                return AssessmentOutcome.Failed($"the agent was stopped: {result.Reply.StopReason}", result.Stats);
            }

            if (result.Structured?.Value is not { } assessment)
            {
                return AssessmentOutcome.Failed($"the agent's answer wasn't usable: {result.Structured?.Error ?? "no answer"}", result.Stats);
            }

            var validation = AssessmentValidator.Validate(assessment, facts);
            return new AssessmentOutcome(assessment.Verdict, validation.Accepted, validation.Rejected, assessment.Summary, result.Stats, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return AssessmentOutcome.Failed($"the agent failed: {ex.GetBaseException().Message}", null);
        }
    }

    internal static IToolPolicy ReadOnlyPolicy(string root)
    {
        var workspace = new WorkspacePolicy(root);
        return ToolPolicy.From(request => request switch
        {
            FileReadRequest read => workspace.EvaluateRead(read.Path),
            ShellRequest shell => workspace.EvaluateShell(shell.CommandLine, shell.WritesFile, shell.PossiblePaths),
            _ => ToolDecision.Reject(ReadOnlyRefusal),
        });
    }
}
