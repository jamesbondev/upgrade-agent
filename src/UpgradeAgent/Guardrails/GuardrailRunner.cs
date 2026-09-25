using System.Text.RegularExpressions;
using UpgradeAgent.Build;
using UpgradeAgent.Infrastructure;

namespace UpgradeAgent.Guardrails;

internal sealed record GuardrailCheck(string Name, bool Passed, string Detail);

internal sealed record GuardrailReport(IReadOnlyList<GuardrailCheck> Checks, IReadOnlyList<string> Warnings)
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

/// <summary>
/// Deterministic checks run by the app after every group. The agent can't see or influence them;
/// a failure reverts the group.
/// </summary>
internal sealed partial class GuardrailRunner(GitCli git)
{
    public async Task<GroupStartState> CaptureStartAsync(string worktree, string commit, CancellationToken cancellationToken)
    {
        var tracked = await git.ListFilesAsync(worktree, cancellationToken);
        return new GroupStartState(
            commit,
            BuildSettingsSnapshot.Take(worktree, tracked),
            await IgnoredFilesAsync(worktree, cancellationToken));
    }

    public async Task<GuardrailReport> RunAsync(
        string worktree,
        GroupStartState start,
        Baseline baseline,
        BuildResult build,
        TestRunResult? tests,
        CancellationToken cancellationToken,
        ReviewContext? review = null)
    {
        var checks = new List<GuardrailCheck>();
        var warnings = new List<string>();

        var head = await git.HeadAsync(worktree, cancellationToken);
        checks.Add(head == start.Commit
            ? new GuardrailCheck("Git state", true, "HEAD unchanged")
            : new GuardrailCheck("Git state", false, $"HEAD moved from {start.Commit.ShortSha()} to {head.ShortSha()}; only the app may commit"));

        checks.Add(build.Succeeded
            ? new GuardrailCheck("Build", true, $"{build.Warnings.Count} warning(s)")
            : new GuardrailCheck("Build", false, $"{build.Errors.Count} error(s)"));

        checks.Add(CheckTests(baseline, tests));

        var diffs = await DiffAsync(worktree, start.Commit, cancellationToken);
        var violations = SuppressionScanner.Scan(diffs);
        checks.Add(violations.Count == 0
            ? new GuardrailCheck("No suppressions or skips", true, $"{diffs.Count} file(s) changed")
            : new GuardrailCheck("No suppressions or skips", false,
                violations.Select(v => $"{v.Rule} in {v.File}").JoinLimited(5)));

        var tracked = await git.ListFilesAsync(worktree, cancellationToken);
        var untracked = await git.ListUntrackedAsync(worktree, cancellationToken);
        var settings = BuildSettingsSnapshot.Take(worktree, tracked.Concat(untracked));
        var added = settings.Except(start.BuildSettings).ToList();
        var removed = start.BuildSettings.Except(settings).ToList();
        checks.Add(added.Count == 0 && removed.Count == 0
            ? new GuardrailCheck("Package versions and build settings", true, "unchanged since the bump")
            : new GuardrailCheck("Package versions and build settings", false,
                removed.Select(r => $"- {r}").Concat(added.Select(a => $"+ {a}")).JoinLimited(4)));

        var deletedTests = diffs.Where(d => d.IsDeleted && baseline.TestFiles.Contains(d.Path)).Select(d => d.Path).ToList();
        var newIgnored = (await IgnoredFilesAsync(worktree, cancellationToken)).Except(start.IgnoredFiles).ToList();
        checks.Add(deletedTests.Count == 0 && newIgnored.Count == 0
            ? new GuardrailCheck("Files", true, "no test files deleted; no new ignored files")
            : new GuardrailCheck("Files", false, string.Join("; ",
                deletedTests.Select(f => $"deleted test file {f}").Concat(newIgnored.Select(f => $"new git-ignored file {f}")))));

        await AddTestFileWarningsAsync(worktree, start.Commit, baseline, diffs, warnings, cancellationToken);
        warnings.AddRange(PublicApiChanges(diffs, baseline.TestFiles));
        if (review is not null)
        {
            warnings.AddRange(ClaimMismatches(diffs, review));
        }

        return new GuardrailReport(checks, warnings);
    }

    /// <summary>
    /// Removed public signatures in non-test code. Often legitimate (an API went async), but callers
    /// outside this repo may break, so a reviewer should look.
    /// </summary>
    internal static IEnumerable<string> PublicApiChanges(IReadOnlyList<FileDiff> diffs, IReadOnlyList<string> testFiles) =>
        diffs
            .Where(d => !d.IsNew && Path.GetExtension(d.Path).Equals(".cs", StringComparison.OrdinalIgnoreCase) && !testFiles.Contains(d.Path))
            .SelectMany(d => SuppressionScanner.Unmatched(d.Removed, d.Added)
                .Where(line => PublicSignature().IsMatch(line))
                .Select(line => $"public API changed in {d.Path}: `{line.Trim().TrimEnd('{').Trim()}`"))
            .Take(8);

    /// <summary>The agent's summary is informational; say where it disagrees with what actually changed.</summary>
    internal static IEnumerable<string> ClaimMismatches(IReadOnlyList<FileDiff> diffs, ReviewContext review)
    {
        if (review.ClaimedFiles is null)
        {
            yield break;
        }

        var changed = diffs.Select(d => RepoPath.Normalize(d.Path)).Except(review.AppChangedFiles.Select(RepoPath.Normalize), StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var claimed = review.ClaimedFiles.Select(RepoPath.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var file in claimed.Where(c => !changed.Contains(c)).Order(StringComparer.Ordinal))
        {
            yield return $"agent summary lists a fix in {file}, but the file is unchanged";
        }

        foreach (var file in changed.Where(c => !claimed.Contains(c)).Order(StringComparer.Ordinal))
        {
            yield return $"changed but not in the agent's summary: {file}";
        }
    }

    internal static GuardrailCheck CheckTests(Baseline baseline, TestRunResult? tests)
    {
        const string Name = "Tests";
        if (tests is null)
        {
            return new GuardrailCheck(Name, false, "not run (build failed)");
        }

        if (!tests.Succeeded)
        {
            var failed = tests.Inventory?.Failed ?? tests.Counts?.Failed;
            return new GuardrailCheck(Name, false, failed is > 0 ? $"{failed} failed" : "dotnet test failed");
        }

        if (baseline.Tests is not null && tests.Inventory is not null)
        {
            var shortfalls = baseline.Tests.Methods
                .Where(m => m.Value.Passed > (tests.Inventory.Methods.TryGetValue(m.Key, out var now) ? now.Passed : 0))
                .Select(m => m.Key)
                .ToList();

            return shortfalls.Count == 0
                ? new GuardrailCheck(Name, true, $"{tests.Inventory.Passed} passed; every baseline test method still passes")
                : new GuardrailCheck(Name, false,
                    $"{shortfalls.Count} test method(s) missing or with fewer passing rows: {shortfalls.JoinLimited(3, ", ")}");
        }

        // No TRX on one side: fall back to totals, and say so.
        var passed = tests.Inventory?.Passed ?? tests.Counts?.Passed;
        return passed is null
            ? new GuardrailCheck(Name, false, "could not read test results")
            : passed >= baseline.PassedCount
                ? new GuardrailCheck(Name, true, $"{passed} passed (count-only check: no TRX, weaker)")
                : new GuardrailCheck(Name, false, $"{passed} passed, baseline {baseline.PassedCount} (count-only check)");
    }

    private async Task<IReadOnlyList<FileDiff>> DiffAsync(string worktree, string commit, CancellationToken cancellationToken)
    {
        var diff = await git.RunAsync(worktree, ["diff", "-U0", "--no-color", "--no-ext-diff", "--no-renames", commit, "--"], cancellationToken);
        var diffs = UnifiedDiff.Parse(diff).ToList();

        foreach (var path in await git.ListUntrackedAsync(worktree, cancellationToken))
        {
            var full = Path.Combine(worktree, path);
            var lines = File.Exists(full) ? await File.ReadAllLinesAsync(full, cancellationToken) : [];
            diffs.Add(new FileDiff(path, lines, [], IsNew: true, IsDeleted: false));
        }

        return diffs;
    }

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

    private async Task AddTestFileWarningsAsync(
        string worktree, string commit, Baseline baseline, IReadOnlyList<FileDiff> diffs, List<string> warnings, CancellationToken cancellationToken)
    {
        foreach (var diff in diffs.Where(d => !d.IsDeleted && baseline.TestFiles.Contains(d.Path)))
        {
            warnings.Add($"test file modified: {diff.Path}");

            var before = CountAssertions(await git.ShowFileAsync(worktree, commit, diff.Path, cancellationToken) ?? "");
            var after = CountAssertions(await File.ReadAllTextAsync(Path.Combine(worktree, diff.Path), cancellationToken));
            if (after < before)
            {
                warnings.Add($"assertion count dropped in {diff.Path}: {before} → {after}");
            }
        }
    }

    internal static int CountAssertions(string code) => Assertion().Count(code);

    [GeneratedRegex(@"(^|/)(bin|obj|TestResults|\.vs|\.idea)(/|$)")]
    private static partial Regex BuildOutput();

    [GeneratedRegex(@"^\s*public\s+(?:(?:static|sealed|abstract|virtual|override|async|partial|readonly|required|new)\s+)*(?:(?:class|record|struct|interface)\b|[\w<>\[\]?,. ]+\()")]
    private static partial Regex PublicSignature();

    [GeneratedRegex(@"\bAssert\.\w+|\.Should\w*\(|\bVerify\w*\(|\bExpect\(")]
    private static partial Regex Assertion();
}
