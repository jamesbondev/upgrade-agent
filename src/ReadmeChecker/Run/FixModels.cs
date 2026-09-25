using AgentHarness;
using AgentHarness.Policies;
using ReadmeChecker.Config;

namespace ReadmeChecker.Run;

internal enum FixStatus
{
    Opened,
    Ready,
    Declined,
    Rejected,
    Failed,
    Skipped,
    NothingToFix,
}

internal sealed record RepoFixReport
{
    public required string Name { get; init; }

    public required string Location { get; init; }

    public required FixStatus Status { get; init; }

    public string? Reason { get; init; }

    public required RepoReport Check { get; init; }

    public string? Branch { get; init; }

    public string? PullRequestUrl { get; init; }

    public string? PatchPath { get; init; }

    public string? AgentSummary { get; init; }

    public AgentStats? FixStats { get; init; }

    public IReadOnlyList<string> Problems { get; init; } = [];

    public TimeSpan Duration { get; init; }

    public double AiCredits => (Check.Stats?.AiCredits ?? 0) + (FixStats?.AiCredits ?? 0);
}

internal sealed record FixRunReport(
    string RunId,
    DateTimeOffset StartedUtc,
    TimeSpan Duration,
    string OutputDirectory,
    double AiCredits,
    IReadOnlyList<RepoFixReport> Repos);

internal sealed record FixArguments(IReadOnlyCollection<string> Only, bool DryRun, IApprovalPrompter Prompter);

internal interface IFixProgress
{
    void RepoStarted(RepoTarget target, int index, int count);

    void RepoFinished(RepoFixReport report);

    void RunFinished(FixRunReport report, string reportPath);
}
