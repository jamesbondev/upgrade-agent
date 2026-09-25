using System.Text.RegularExpressions;
using UpgradeAgent.Infrastructure;

namespace UpgradeAgent.Guardrails;

internal sealed partial class TestFileNotes(GitCli git) : IReviewNoteSource
{
    public async Task<IReadOnlyList<ReviewNote>> CollectAsync(GuardrailContext context, CancellationToken cancellationToken)
    {
        var notes = new List<ReviewNote>();
        foreach (var diff in context.Diffs.Where(d => !d.IsDeleted && context.IsTestFile(d.Path)))
        {
            notes.Add(new(ReviewNoteKind.TestFileModified, $"test file modified: {diff.Path}"));

            var before = CountAssertions(await git.ShowFileAsync(context.Input.Worktree, context.Input.Start.Commit, diff.Path, cancellationToken) ?? "");
            var after = CountAssertions(await File.ReadAllTextAsync(Path.Combine(context.Input.Worktree, diff.Path), cancellationToken));
            if (after < before)
            {
                notes.Add(new(ReviewNoteKind.AssertionsDropped, $"assertion count dropped in {diff.Path}: {before} → {after}"));
            }
        }

        return notes;
    }

    internal static int CountAssertions(string code) => Assertion().Count(code);

    [GeneratedRegex(@"\bAssert\.\w+|\.Should\w*\(|\bVerify\w*\(|\bExpect\(")]
    private static partial Regex Assertion();
}

internal sealed partial class PublicApiNotes : IReviewNoteSource
{
    private const int MaxNotes = 8;

    public Task<IReadOnlyList<ReviewNote>> CollectAsync(GuardrailContext context, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ReviewNote>>(Find(context.Diffs, context.IsTestFile).ToList());

    internal static IEnumerable<ReviewNote> Find(IReadOnlyList<FileDiff> diffs, Func<string, bool> isTestFile) =>
        diffs
            .Where(d => !d.IsNew && Path.GetExtension(d.Path).Equals(".cs", StringComparison.OrdinalIgnoreCase) && !isTestFile(d.Path))
            .SelectMany(d => d.GenuinelyRemoved
                .Where(line => PublicSignature().IsMatch(line))
                .Select(line => new ReviewNote(ReviewNoteKind.PublicApiChanged, $"public API changed in {d.Path}: `{line.Trim().TrimEnd('{').Trim()}`")))
            .Take(MaxNotes);

    [GeneratedRegex(@"^\s*public\s+(?:(?:static|sealed|abstract|virtual|override|async|partial|readonly|required|new)\s+)*(?:(?:class|record|struct|interface)\b|[\w<>\[\]?,. ]+\()")]
    private static partial Regex PublicSignature();
}

internal sealed class ClaimNotes : IReviewNoteSource
{
    public Task<IReadOnlyList<ReviewNote>> CollectAsync(GuardrailContext context, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ReviewNote>>(Find(context.Diffs, context.Input.Review).ToList());

    internal static IEnumerable<ReviewNote> Find(IReadOnlyList<FileDiff> diffs, ReviewContext review)
    {
        if (review.ClaimedFiles is null)
        {
            yield break;
        }

        var changed = diffs.Select(d => RepoPath.Normalize(d.Path))
            .Except(review.AppChangedFiles.Select(RepoPath.Normalize), StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var claimed = review.ClaimedFiles.Select(RepoPath.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var file in claimed.Where(c => !changed.Contains(c)).Order(StringComparer.Ordinal))
        {
            yield return new(ReviewNoteKind.SummaryMismatch, $"agent summary lists a fix in {file}, but the file is unchanged");
        }

        foreach (var file in changed.Where(c => !claimed.Contains(c)).Order(StringComparer.Ordinal))
        {
            yield return new(ReviewNoteKind.SummaryMismatch, $"changed but not in the agent's summary: {file}");
        }
    }
}
