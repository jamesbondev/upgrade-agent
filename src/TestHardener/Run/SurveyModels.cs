using TestHardener.Analysis;
using TestHardener.Config;
using TestHardener.Stryker;

namespace TestHardener.Run;

internal enum SurveyStatus
{
    Surveyed,
    Partial,
    Failed,
}

internal sealed record TargetSurvey
{
    public required string Name { get; init; }

    public required string Project { get; init; }

    public IReadOnlyList<string> TestProjects { get; init; } = [];

    public string? Failure { get; init; }

    public string? ReportPath { get; init; }

    public IReadOnlyDictionary<MutantStatus, int> StatusCounts { get; init; } = new Dictionary<MutantStatus, int>();

    public double? Score { get; init; }

    public int Tests { get; init; }

    public int Survivors { get; init; }

    public IReadOnlyDictionary<SkipReason, int> Skipped { get; init; } = new Dictionary<SkipReason, int>();

    public IReadOnlyList<SurvivorGroup> Groups { get; init; } = [];

    public IReadOnlyList<NotMutatedMember> NotMutated { get; init; } = [];

    public IReadOnlyList<UntestedFile> Untested { get; init; } = [];

    public TimeSpan Duration { get; init; }

    public int Candidates => Groups.Sum(g => g.Survivors.Count);
}

internal sealed record RepoSurvey
{
    public required string Name { get; init; }

    public required string Location { get; init; }

    public required SurveyStatus Status { get; init; }

    public string? Note { get; init; }

    public string? Sha { get; init; }

    public string? CloneRoot { get; init; }

    public IReadOnlyList<TargetSurvey> Targets { get; init; } = [];

    public TimeSpan Duration { get; init; }

    public static RepoSurvey Failed(RepoTarget target, string note) =>
        new() { Name = target.Name, Location = target.Location, Status = SurveyStatus.Failed, Note = note };
}

internal sealed record SurveyReport(
    string RunId,
    DateTimeOffset StartedUtc,
    TimeSpan Duration,
    string OutputDirectory,
    string? From,
    IReadOnlyList<RepoSurvey> Repos);

internal sealed record SurveyArguments(IReadOnlyCollection<string> Only, string? From);

internal interface ISurveyProgress
{
    void RepoStarted(RepoTarget target, int index, int count);

    void TargetStarted(TargetConfig target, bool fromEarlierRun);

    void TargetFinished(TargetSurvey target);

    void RepoFinished(RepoSurvey repo);

    void RunFinished(SurveyReport report, string reportPath);
}
