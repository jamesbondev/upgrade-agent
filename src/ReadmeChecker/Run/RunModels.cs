using AgentHarness;
using ReadmeChecker.Agent;
using ReadmeChecker.Config;
using ReadmeChecker.Detection;

namespace ReadmeChecker.Run;

internal enum RepoVerdict
{
    Stale,
    Unsure,
    Missing,
    Error,
    Unassessed,
    Current,
}

internal sealed record RepoReport
{
    public required string Name { get; init; }

    public required string Location { get; init; }

    public required RepoVerdict Verdict { get; init; }

    public bool Deterministic { get; init; }

    public string? Note { get; init; }

    public string? ReadmePath { get; init; }

    public DateTimeOffset? ReadmeLastChanged { get; init; }

    public int? CommitsSinceReadme { get; init; }

    public IReadOnlyList<Signal> Signals { get; init; } = [];

    public int SignalsNotListed { get; init; }

    public IReadOnlyList<ReadmeIssue> Issues { get; init; } = [];

    public IReadOnlyList<RejectedIssue> Unsupported { get; init; } = [];

    public string? AgentSummary { get; init; }

    public AgentStats? Stats { get; init; }

    public Coverage? Coverage { get; init; }

    public TimeSpan Duration { get; init; }

    public static RepoReport For(RepoTarget target, RepoVerdict verdict, string? note = null) =>
        new() { Name = target.Name, Location = target.Location, Verdict = verdict, Note = note };
}

internal sealed record CheckReport(
    string RunId,
    DateTimeOffset StartedUtc,
    TimeSpan Duration,
    string OutputDirectory,
    double AiCredits,
    IReadOnlyList<RepoReport> Repos);

internal sealed record CheckArguments(IReadOnlyCollection<string> Only, AgentProvider Provider, bool Deep = false);

internal interface ICheckProgress
{
    void RepoStarted(RepoTarget target, int index, int count);

    void RepoFinished(RepoReport report);

    void RunFinished(CheckReport report, string reportPath);
}
