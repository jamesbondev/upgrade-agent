using System.Text.RegularExpressions;
using UpgradeAgent.Infrastructure;

namespace UpgradeAgent.Guardrails;

/// <summary>
/// Runs the deterministic checks after every group. It gathers the evidence once (diff, build settings,
/// ignored files) and hands the same context to every check and note source; adding a check means adding an
/// <see cref="IGuardrail"/>, not editing this class.
/// </summary>
internal sealed partial class GuardrailRunner(GitCli git, IEnumerable<IGuardrail> guardrails, IEnumerable<IReviewNoteSource> noteSources)
{
    private const int BinarySniffLength = 8000;

    public async Task<GroupStartState> CaptureStartAsync(string worktree, string commit, CancellationToken cancellationToken)
    {
        var tracked = await git.ListFilesAsync(worktree, cancellationToken);
        return new GroupStartState(commit, BuildSettingsSnapshot.Take(worktree, tracked), await IgnoredFilesAsync(worktree, cancellationToken));
    }

    public async Task<GuardrailReport> RunAsync(GuardrailInput input, CancellationToken cancellationToken)
    {
        var context = await GatherAsync(input, cancellationToken);
        var checks = guardrails.Select(g => g.Check(context)).ToList();
        var notes = new List<ReviewNote>();
        foreach (var source in noteSources)
        {
            notes.AddRange(await source.CollectAsync(context, cancellationToken));
        }

        return new GuardrailReport(checks, notes);
    }

    private async Task<GuardrailContext> GatherAsync(GuardrailInput input, CancellationToken cancellationToken)
    {
        var worktree = input.Worktree;
        var head = await git.HeadAsync(worktree, cancellationToken);
        var tracked = await git.ListFilesAsync(worktree, cancellationToken);
        var untracked = await git.ListUntrackedAsync(worktree, cancellationToken);

        var diff = await git.RunAsync(worktree, ["diff", "-U0", "--no-color", "--no-ext-diff", "--no-renames", input.Start.Commit, "--"], cancellationToken);
        var diffs = UnifiedDiff.Parse(diff).ToList();
        foreach (var path in untracked)
        {
            diffs.Add(new FileDiff(path, await ReadTextLinesAsync(Path.Combine(worktree, path), cancellationToken), [], IsNew: true, IsDeleted: false));
        }

        return new GuardrailContext(
            input,
            head,
            diffs,
            BuildSettingsSnapshot.Take(worktree, tracked.Concat(untracked)),
            await IgnoredFilesAsync(worktree, cancellationToken));
    }

    /// <summary>
    /// The lines of a new text file, decoded by its byte-order mark (UTF-16 source is valid C#, so it must be scanned).
    /// None for a binary file: no BOM and a NUL in the first bytes.
    /// </summary>
    private static async Task<IReadOnlyList<string>> ReadTextLinesAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (!HasByteOrderMark(bytes) && bytes.AsSpan(0, Math.Min(bytes.Length, BinarySniffLength)).Contains((byte)0))
        {
            return [];
        }

        using var reader = new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true);
        return (await reader.ReadToEndAsync(cancellationToken)).ReplaceLineEndings("\n").Split('\n');
    }

    private static bool HasByteOrderMark(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])
        || bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE])
        || bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF])
        || bytes.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0xFE, 0xFF]);

    /// <summary>
    /// Ignored files outside build output. A <c>*.csproj.user</c> is ignored by default yet imported by
    /// MSBuild, so it could change the build invisibly to the diff checks.
    /// </summary>
    private async Task<IReadOnlySet<string>> IgnoredFilesAsync(string worktree, CancellationToken cancellationToken) =>
        (await git.StatusAsync(worktree, includeIgnored: true, cancellationToken))
            .Where(l => l.StartsWith("!! ", StringComparison.Ordinal))
            .Select(l => l[3..].Trim('"'))
            .Where(p => !BuildOutput().IsMatch(p))
            .ToHashSet(StringComparer.Ordinal);

    [GeneratedRegex(@"(^|/)(bin|obj|TestResults|\.vs|\.idea)(/|$)")]
    private static partial Regex BuildOutput();
}
