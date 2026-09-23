using System.Diagnostics;
using System.Text.Json;
using UpgradeAgent.Build;
using UpgradeAgent.Bumping;
using UpgradeAgent.Config;
using UpgradeAgent.Detection;
using UpgradeAgent.Guardrails;
using UpgradeAgent.Infrastructure;
using UpgradeAgent.Ui;
using UpgradeAgent.Workspace;

namespace UpgradeAgent.Run;

public sealed class RunAbortedException(string message) : Exception(message);

/// <summary>
/// The deterministic spine of a run: worktree, baseline, plan, then per group bump → restore → build →
/// test → (fix) → rebuild → retest → guardrails → commit or revert.
/// </summary>
public sealed class RunOrchestrator(
    ResolvedConfig config,
    IProcessRunner processRunner,
    IGroupFixer fixer,
    RunRenderer renderer,
    TimeProvider? timeProvider = null)
{
    private static readonly string[] IncompatibleFrameworkCodes = ["NU1201", "NU1202", "NU1203"];

    private readonly GitCli _git = new(processRunner);
    private readonly DotnetCli _dotnet = new(processRunner);
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<RunReport> RunAsync(IReadOnlyCollection<string> only, string? planFile, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var started = _time.GetUtcNow();
        var targetCommit = await _git.HeadAsync(config.RepoPath, cancellationToken);

        var workRoot = string.IsNullOrWhiteSpace(config.Options.Target.WorkRoot)
            ? RunWorkspace.DefaultWorkRoot(config.RepoPath)
            : Path.GetFullPath(config.Options.Target.WorkRoot, config.RepoPath);
        var workspace = await RunWorkspace.CreateAsync(_git, config.RepoPath, config.SolutionPath, workRoot, config.OutputDirectory, started, cancellationToken);
        Directory.CreateDirectory(workspace.OutputDirectory);
        renderer.Workspace(workspace);

        var leaks = RunWorkspace.FindConfigLeaks(config.RepoPath, workspace.WorktreePath);
        if (leaks.Count > 0)
        {
            throw new RunAbortedException(
                $"The worktree inherits config files the repo doesn't: {string.Join(", ", leaks)}. Set Target:WorkRoot to a folder beside the repo.");
        }

        var forceEvaluate = Directory.EnumerateFiles(workspace.WorktreePath, "packages.lock.json", SearchOption.AllDirectories).Any();
        var runnerMode = DotnetCli.DetectRunnerMode(workspace.WorktreePath);
        var baseline = await GetBaselineAsync(workspace, targetCommit, runnerMode, forceEvaluate, cancellationToken);

        var plan = planFile is not null
            ? LoadPlan(planFile)
            : await DetectAsync(workspace, only, cancellationToken);
        renderer.Plan(plan, workspace.WorktreePath);
        await File.WriteAllTextAsync(Path.Combine(workspace.OutputDirectory, "plan.json"), JsonSerializer.Serialize(plan, JsonDefaults.Options), cancellationToken);

        var results = new List<GroupResult>();
        var ledger = new List<string>();
        for (var i = 0; i < plan.Groups.Count; i++)
        {
            var group = plan.Groups[i];
            renderer.GroupHeader(group, i + 1, plan.Groups.Count);
            var result = await RunGroupAsync(workspace, group, baseline, runnerMode, forceEvaluate, cancellationToken);
            results.Add(result);
            if (result is { Status: GroupStatus.Accepted, Commit: not null })
            {
                ledger.Add(result.Commit);
            }

            renderer.GroupOutcome(result);
            if (result.Status == GroupStatus.Cancelled)
            {
                break;
            }
        }

        var report = new RunReport(
            workspace.RunId, workspace.BranchName, workspace.WorktreePath, targetCommit, started, stopwatch.Elapsed, plan, results, ledger);
        await File.WriteAllTextAsync(Path.Combine(workspace.OutputDirectory, "run.json"), JsonSerializer.Serialize(report, JsonDefaults.Options), CancellationToken.None);

        var log = await _git.RunAsync(workspace.WorktreePath, ["log", "--oneline", "--no-decorate", $"{targetCommit}..HEAD"], CancellationToken.None);
        renderer.Summary(report, GitCli.SplitLines(log));
        return report;
    }

    private async Task<GroupResult> RunGroupAsync(
        RunWorkspace workspace, UpdateGroup group, Baseline baseline, TestRunnerMode runnerMode, bool forceEvaluate, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var worktree = workspace.WorktreePath;
        var start = await _git.HeadAsync(worktree, cancellationToken);

        GroupResult Result(GroupStatus status, string? reason, BumpResult? bump = null, GuardrailReport? guardrails = null, FixOutcome? fix = null, string? commit = null) =>
            new(group.Name, group.Kind, status, reason, commit, bump?.Edits ?? [], bump?.Manual ?? [], guardrails, fix, stopwatch.Elapsed);

        try
        {
            var bump = VersionBumper.Apply(worktree, group.Updates);
            renderer.Bump(bump);
            if (bump.Edits.Count == 0)
            {
                return Result(GroupStatus.NothingToDo, "no version entries to change", bump);
            }

            var restore = await _dotnet.RestoreAsync(workspace.SolutionPath, forceEvaluate, cancellationToken);
            if (!restore.Succeeded)
            {
                await RevertAsync(worktree, start);
                var incompatible = restore.Errors.Any(e => IncompatibleFrameworkCodes.Contains(e.Code));
                renderer.Build("Restore", restore);
                return Result(GroupStatus.Rejected, incompatible ? "needs TFM upgrade (restore: package not compatible with project framework)" : "restore failed", bump);
            }

            var startState = await new GuardrailRunner(_git).CaptureStartAsync(worktree, start, cancellationToken);
            var (build, tests) = await BuildAndTestAsync(workspace, runnerMode, "after-bump", cancellationToken);

            FixOutcome? fix = null;
            if (!build.Succeeded || tests is not { Succeeded: true })
            {
                fix = await fixer.FixAsync(new FixContext(workspace, group, bump, build, tests), cancellationToken);
                renderer.Fix(fix);
                if (fix.Attempted)
                {
                    (build, tests) = await BuildAndTestAsync(workspace, runnerMode, "after-fix", cancellationToken);
                }
            }

            var guardrails = await new GuardrailRunner(_git).RunAsync(worktree, startState, baseline, build, tests, cancellationToken);
            renderer.Guardrails(guardrails);

            if (!guardrails.Passed)
            {
                await RevertAsync(worktree, start);
                var reason = fix is { Attempted: false } ? $"{guardrails.FailureSummary} ({fix.Summary})" : guardrails.FailureSummary;
                return Result(GroupStatus.Rejected, reason, bump, guardrails, fix);
            }

            var commit = await CommitAsync(workspace, group, bump, cancellationToken);
            return Result(GroupStatus.Accepted, null, bump, guardrails, fix, commit);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await RevertAsync(worktree, start);
            return Result(GroupStatus.Cancelled, "cancelled; changes reverted");
        }
    }

    private async Task<(BuildResult Build, TestRunResult? Tests)> BuildAndTestAsync(
        RunWorkspace workspace, TestRunnerMode runnerMode, string label, CancellationToken cancellationToken)
    {
        var build = await _dotnet.BuildAsync(workspace.SolutionPath, cancellationToken);
        renderer.Build("Build", build);
        if (!build.Succeeded)
        {
            return (build, null);
        }

        var tests = await _dotnet.TestAsync(
            workspace.SolutionPath, Path.Combine(workspace.OutputDirectory, "trx", label), runnerMode, config.Options.Target.TestArgs, cancellationToken);
        renderer.Tests(tests);
        return (build, tests);
    }

    private async Task<Baseline> GetBaselineAsync(
        RunWorkspace workspace, string commit, TestRunnerMode runnerMode, bool forceEvaluate, CancellationToken cancellationToken)
    {
        var sdk = await _dotnet.SdkVersionAsync(workspace.WorktreePath, cancellationToken);
        var cache = new BaselineCache(Path.Combine(workspace.WorkRoot, "baseline"));
        var cachePath = cache.PathFor(commit, sdk, config.Options.Target.TestArgs);
        if (cache.TryLoad(cachePath) is { } cached)
        {
            renderer.Baseline(cached, fromCache: true);
            return cached;
        }

        renderer.Status("Baseline: restore, build and test at the starting commit...");
        var restore = await _dotnet.RestoreAsync(workspace.SolutionPath, forceEvaluate, cancellationToken);
        if (!restore.Succeeded)
        {
            renderer.Build("Restore", restore);
            throw new RunAbortedException("Baseline restore failed. Check feed access before running.");
        }

        var (build, tests) = await BuildAndTestAsync(workspace, runnerMode, "baseline", cancellationToken);
        if (!build.Succeeded || tests is not { Succeeded: true })
        {
            throw new RunAbortedException("The baseline is red: the repo must build and pass its tests before UpgradeAgent changes anything.");
        }

        var tracked = await _git.ListFilesAsync(workspace.WorktreePath, cancellationToken);
        var baseline = new Baseline(
            commit, sdk, runnerMode, tests.Inventory, tests.Counts,
            TestProjects.FindTestFiles(workspace.WorktreePath, tracked).Order(StringComparer.Ordinal).ToList(),
            _time.GetUtcNow());
        cache.Save(cachePath, baseline);
        renderer.Baseline(baseline, fromCache: false);
        return baseline;
    }

    private async Task<UpgradePlan> DetectAsync(RunWorkspace workspace, IReadOnlyCollection<string> only, CancellationToken cancellationToken)
    {
        renderer.Status("Detecting outdated packages (latest, highest minor, highest patch)...");
        var reports = await new PackageListRunner(processRunner)
            .ListAllAsync(workspace.SolutionPath, config.Options.Policy.IncludePrerelease, cancellationToken);
        var planner = new Planner(config.Options.Policy, new NuGetPackageCompatibilityChecker(workspace.WorktreePath), _time);
        return await planner.CreateAsync(reports, workspace.WorktreePath, workspace.SolutionPath, only, cancellationToken);
    }

    private static UpgradePlan LoadPlan(string planFile) =>
        JsonSerializer.Deserialize<UpgradePlan>(File.ReadAllText(planFile), JsonDefaults.Options)
        ?? throw new RunAbortedException($"Could not read plan file {planFile}.");

    private async Task<string> CommitAsync(RunWorkspace workspace, UpdateGroup group, BumpResult bump, CancellationToken cancellationToken)
    {
        var worktree = workspace.WorktreePath;

        // Tracked changes plus new source files only. Anything else new is dropped, never committed blindly.
        await _git.RunAsync(worktree, ["add", "--update"], cancellationToken);
        var newSources = (await _git.ListUntrackedAsync(worktree, cancellationToken))
            .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".cs" or ".fs" or ".vb")
            .ToList();
        if (newSources.Count > 0)
        {
            await _git.RunAsync(worktree, ["add", "--", .. newSources], cancellationToken);
        }

        var message = CommitMessage.Create(group.Name, bump.Edits, workspace.RunId);
        List<string> identity = await _git.HasIdentityAsync(worktree, cancellationToken)
            ? []
            : ["-c", "user.name=UpgradeAgent", "-c", "user.email=upgrade-agent@localhost"];
        await _git.RunAsync(worktree, [.. identity, "commit", "--no-verify", "-q", "-m", message], cancellationToken);
        await _git.RunAsync(worktree, ["clean", "-fd"], cancellationToken);

        return await _git.HeadAsync(worktree, cancellationToken);
    }

    /// <summary>Runs even when the run is being cancelled: a half-applied group must never survive.</summary>
    private async Task RevertAsync(string worktree, string commit)
    {
        await _git.RunAsync(worktree, ["reset", "--hard", "-q", commit], CancellationToken.None);
        await _git.RunAsync(worktree, ["clean", "-fd", "-q"], CancellationToken.None);
    }
}
