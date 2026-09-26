using AgentHarness;
using AgentHarness.Policies;
using ReadmeChecker.Config;
using ReadmeChecker.Detection;
using RepoKit;

namespace ReadmeChecker.Agent;

internal enum ChunkStatus
{
    Checked,
    PartlyChecked,
    NotChecked,
    Skipped,
}

internal sealed record ChunkCoverage(int Part, int FirstLine, int LastLine, ChunkStatus Status, int ClaimsChecked, double AiCredits, string? Note);

internal sealed record LineRange(int FirstLine, int LastLine);

internal sealed record Coverage(int ClaimsChecked, IReadOnlyList<ChunkCoverage> Parts, IReadOnlyList<LineRange> NotChecked);

internal sealed record AssessmentOutcome(
    AssessedVerdict? Verdict,
    IReadOnlyList<ReadmeIssue> Issues,
    IReadOnlyList<RejectedIssue> Rejected,
    string? Summary,
    AgentStats? Stats,
    string? Failure,
    Coverage? Coverage = null)
{
    public static AssessmentOutcome Failed(string failure, AgentStats? stats) => new(null, [], [], null, stats, failure);
}

internal interface IReadmeAssessor
{
    Task<AssessmentOutcome> AssessAsync(
        string repoName, string root, RepoFacts facts, SignalScan scan, string logPath, double? creditBudget, CancellationToken cancellationToken);
}

internal sealed class ReadmeAssessor(IAgentBackendFactory backends, AgentOptions options, GitCli git, TimeProvider time) : IReadmeAssessor
{
    public const string ReadOnlyRefusal = "This session is read-only: read files and run read-only commands, then answer. Don't change anything.";

    public async Task<AssessmentOutcome> AssessAsync(
        string repoName, string root, RepoFacts facts, SignalScan scan, string logPath, double? creditBudget, CancellationToken cancellationToken)
    {
        await using var backend = backends.Create();
        await backends.EnsureReadyAsync(backend, root, cancellationToken);
        await using var log = AgentLog.Open(logPath, time);

        var result = await AgentSessions.ExploreThenAskAsync<ReadmeAssessment>(
            backend, SessionOptions($"readme {repoName}", root, AssessmentPrompts.System, options, log),
            AssessmentPrompts.Task(repoName, facts, scan), AssessmentPrompts.Question, askAfterStop: false, time, cancellationToken);
        AgentSessions.ThrowIfQuotaExceeded(result);

        if (result.Failure is { } failure)
        {
            return AssessmentOutcome.Failed(failure, result.Stats);
        }

        if (result.Reply is { Stopped: true } stopped)
        {
            return AssessmentOutcome.Failed($"the agent was stopped: {stopped.StopReason}", result.Stats);
        }

        if (result.Structured?.Value is not { } assessment)
        {
            return AssessmentOutcome.Failed($"the agent's answer wasn't usable: {result.Structured?.Error ?? "no answer"}", result.Stats);
        }

        var validation = await AssessmentValidator.ValidateAsync(assessment.Issues, new ValidationScope(root, git, facts, scan.Signals), cancellationToken);
        return new AssessmentOutcome(assessment.Verdict, validation.Accepted, validation.Rejected, assessment.Summary, result.Stats, null);
    }

    internal static AgentSessionOptions SessionOptions(string name, string root, string instructions, AgentOptions options, AgentLog log) => new()
    {
        Name = name,
        WorkingDirectory = root,
        Instructions = instructions,
        Policy = ReadOnlyPolicy(root),
        Limits = new AgentLimits
        {
            MaxDuration = TimeSpan.FromMinutes(options.MaxMinutes),
            MaxToolCalls = options.MaxToolCalls,
            MaxRefusals = options.MaxRefusals,
        },
        Observers = [log.Observer],
    };

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
