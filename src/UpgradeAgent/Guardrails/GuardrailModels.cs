using UpgradeAgent.Build;

namespace UpgradeAgent.Guardrails;

internal sealed record GuardrailCheck(string Name, bool Passed, string Detail);

internal enum ReviewNoteKind
{
    TestFileModified,
    AssertionsDropped,
    PublicApiChanged,
    SummaryMismatch,
}

/// <summary>Not a failure, but something a human reviewer should look at.</summary>
internal sealed record ReviewNote(ReviewNoteKind Kind, string Message)
{
    public override string ToString() => Message;
}

internal sealed record GuardrailReport(IReadOnlyList<GuardrailCheck> Checks, IReadOnlyList<ReviewNote> Notes)
{
    public bool Passed => Checks.All(c => c.Passed);

    public string FailureSummary => string.Join("; ", Checks.Where(c => !c.Passed).Select(c => $"{c.Name}: {c.Detail}"));
}

/// <summary>What the reviewer notes compare against: the files the agent says it fixed, and the files the app itself changed.</summary>
internal sealed record ReviewContext(IReadOnlyCollection<string>? ClaimedFiles, IReadOnlyCollection<string> AppChangedFiles);

/// <summary>State captured when a group starts (after the app's bump), compared when it ends.</summary>
internal sealed record GroupStartState(
    string Commit,
    IReadOnlySet<string> BuildSettings,
    IReadOnlySet<string> IgnoredFiles);

/// <summary>What the caller hands the guardrails after a group's final build and test run.</summary>
internal sealed record GuardrailInput(
    string Worktree,
    GroupStartState Start,
    Baseline Baseline,
    BuildResult Build,
    TestRunResult? Tests,
    ReviewContext Review);

/// <summary>Everything the checks look at, gathered from git and the disk once.</summary>
internal sealed class GuardrailContext(
    GuardrailInput input,
    string head,
    IReadOnlyList<FileDiff> diffs,
    IReadOnlySet<string> buildSettings,
    IReadOnlySet<string> ignoredFiles)
{
    private readonly HashSet<string> _testFiles = input.Baseline.TestFiles.ToHashSet(StringComparer.Ordinal);

    public GuardrailInput Input => input;

    public string Head => head;

    public IReadOnlyList<FileDiff> Diffs => diffs;

    /// <summary>The same snapshot as <see cref="GroupStartState.BuildSettings"/>, taken now.</summary>
    public IReadOnlySet<string> BuildSettings => buildSettings;

    public IReadOnlySet<string> IgnoredFiles => ignoredFiles;

    public bool IsTestFile(string path) => _testFiles.Contains(path);
}

/// <summary>One deterministic check. The agent can't see or influence it; a failure reverts the group.</summary>
internal interface IGuardrail
{
    GuardrailCheck Check(GuardrailContext context);
}

/// <summary>Produces reviewer notes. Notes never reject a group.</summary>
internal interface IReviewNoteSource
{
    Task<IReadOnlyList<ReviewNote>> CollectAsync(GuardrailContext context, CancellationToken cancellationToken);
}
