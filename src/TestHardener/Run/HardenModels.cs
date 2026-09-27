using AgentHarness.Policies;
using TestHardener.Analysis;
using TestHardener.Config;
using TestHardener.Hardening;

namespace TestHardener.Run;

internal enum HardenStatus
{
    Opened,
    Ready,
    Declined,
    Rejected,
    Skipped,
    NothingToDo,
    Failed,
}

internal sealed record RepoHardenReport
{
    public required string Name { get; init; }

    public required string Location { get; init; }

    public required HardenStatus Status { get; init; }

    public string? Note { get; init; }

    public string? Sha { get; init; }

    public IReadOnlyList<GroupResult> Groups { get; init; } = [];

    public IReadOnlyList<string> ChangedFiles { get; init; } = [];

    public string? PatchPath { get; init; }

    public string? Branch { get; init; }

    public string? PullRequestUrl { get; init; }

    public IReadOnlyList<string> LeftoverBranches { get; init; } = [];

    public double AiCredits { get; init; }

    public TimeSpan Duration { get; init; }

    public int Verified => Groups.Count(g => g.Outcome == GroupOutcome.Verified);

    public int Killed => Groups.Where(g => g.Outcome == GroupOutcome.Verified).Sum(g => g.Final?.Killed ?? 0);

    public int Targeted => Groups.Where(g => g.Outcome == GroupOutcome.Verified).Sum(g => g.Final?.Survivors.Count ?? 0);

    public int TestsAdded => Groups.Where(g => g.Outcome == GroupOutcome.Verified).Sum(g => g.Final?.NewTests.Count ?? 0);
}

internal sealed record HardenReport(
    string RunId,
    DateTimeOffset StartedUtc,
    TimeSpan Duration,
    string OutputDirectory,
    bool DryRun,
    double AiCredits,
    IReadOnlyList<RepoHardenReport> Repos);

internal sealed record HardenArguments(IReadOnlyCollection<string> Only, string? From, bool DryRun, IApprovalPrompter Prompter);

internal sealed record PlannedGroup(TargetConfig Target, SurvivorGroup Group);

internal interface IHardenProgress
{
    void RepoStarted(RepoTarget target, int index, int count);

    void GroupStarted(int number, int count, PlannedGroup planned);

    void GroupFinished(GroupResult result);

    void RepoFinished(RepoHardenReport report);

    void RunFinished(HardenReport report, string reportPath);
}
