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

internal sealed record ReviewNote(ReviewNoteKind Kind, string Message)
{
    public override string ToString() => Message;
}

internal sealed record GuardrailReport(IReadOnlyList<GuardrailCheck> Checks, IReadOnlyList<ReviewNote> Notes)
{
    public bool Passed => Checks.All(c => c.Passed);

    public string FailureSummary => string.Join("; ", Checks.Where(c => !c.Passed).Select(c => $"{c.Name}: {c.Detail}"));
}

internal sealed record ReviewContext(IReadOnlyCollection<string>? ClaimedFiles, IReadOnlyCollection<string> AppChangedFiles);

internal sealed record GroupStartState(
    string Commit,
    IReadOnlySet<string> BuildSettings,
    IReadOnlySet<string> IgnoredFiles);

internal sealed record GuardrailInput(
    string Worktree,
    GroupStartState Start,
    Baseline Baseline,
    BuildResult Build,
    TestRunResult? Tests,
    ReviewContext Review);

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

    public IReadOnlySet<string> BuildSettings => buildSettings;

    public IReadOnlySet<string> IgnoredFiles => ignoredFiles;

    public bool IsTestFile(string path) => _testFiles.Contains(path);
}

internal interface IGuardrail
{
    GuardrailCheck Check(GuardrailContext context);
}

internal interface IReviewNoteSource
{
    Task<IReadOnlyList<ReviewNote>> CollectAsync(GuardrailContext context, CancellationToken cancellationToken);
}
