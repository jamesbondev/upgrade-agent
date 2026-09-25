using System.ComponentModel;

namespace ReadmeChecker.Agent;

internal enum AssessedVerdict
{
    Current,
    Stale,
    Unsure,
}

internal enum IssueKind
{
    BrokenReference,
    WrongCommand,
    OutdatedVersion,
    MissingContent,
    Other,
}

internal sealed record ReadmeAssessment
{
    [Description("Current: nothing needs fixing. Stale: you confirmed at least one issue. Unsure: you could not check enough to tell.")]
    public required AssessedVerdict Verdict { get; init; }

    [Description("Each problem you confirmed by looking at the repository. Empty when the verdict is Current.")]
    public IReadOnlyList<ReadmeIssue> Issues { get; init => field = value ?? []; } = [];

    [Description("Two or three sentences for the person who will review this.")]
    public required string Summary { get; init; }
}

internal sealed record ReadmeIssue
{
    public required IssueKind Kind { get; init; }

    [Description("Text copied exactly from the README: the wrong text, or for missing content the heading or sentence nearest to where it belongs.")]
    public required string Quote { get; init; }

    [Description("Repository-relative paths of files or folders that exist and show the problem, e.g. the project that was added or the file that replaced a moved one.")]
    public IReadOnlyList<string> Evidence { get; init => field = value ?? []; } = [];

    [Description("What the README should say instead, in one or two sentences.")]
    public required string SuggestedFix { get; init; }
}
