using UpgradeAgent.Build;
using UpgradeAgent.Config;
using UpgradeAgent.Infrastructure;
using UpgradeAgent.Run;
using UpgradeAgent.Workspace;

namespace UpgradeAgent.Guardrails;

/// <summary>
/// Green-before-we-start: restores, builds and tests the untouched worktree, or reuses a cached baseline for
/// the same commit, SDK and test arguments. A red baseline aborts the run: nothing can be verified against it.
/// </summary>
internal sealed class BaselineProvider(DotnetCli dotnet, GitCli git, TargetOptions target, IRunProgress progress, TimeProvider time)
{
    public async Task<Baseline> GetAsync(RunWorkspace workspace, string commit, TestRunnerMode runnerMode, bool forceEvaluate, CancellationToken cancellationToken)
    {
        var sdk = await dotnet.SdkVersionAsync(workspace.WorktreePath, cancellationToken);
        var cache = new BaselineCache(Path.Combine(workspace.WorkRoot, "baseline"));
        if (cache.TryLoad(commit, sdk, target.TestArgs) is { } cached)
        {
            progress.BaselineReady(cached, fromCache: true);
            return cached;
        }

        progress.Status("Baseline: restore, build and test at the starting commit...");
        var restore = await dotnet.RestoreAsync(workspace.SolutionPath, forceEvaluate, cancellationToken);
        if (!restore.Succeeded)
        {
            progress.Built("Restore", restore);
            throw new RunAbortedException("Baseline restore failed. Check feed access before running.");
        }

        var build = await dotnet.BuildAsync(workspace.SolutionPath, cancellationToken);
        progress.Built("Build", build);
        var tests = build.Succeeded
            ? await dotnet.TestAsync(workspace.SolutionPath, Path.Combine(workspace.OutputDirectory, "trx", "baseline"), runnerMode, target.TestArgs, cancellationToken)
            : null;
        if (tests is not null)
        {
            progress.Tested(tests);
        }

        if (!build.Succeeded || tests is not { Succeeded: true })
        {
            throw new RunAbortedException("The baseline is red: the repo must build and pass its tests before UpgradeAgent changes anything.");
        }

        var tracked = await git.ListFilesAsync(workspace.WorktreePath, cancellationToken);
        var baseline = new Baseline(
            commit, sdk, runnerMode, tests.Inventory, tests.Counts,
            TestProjects.FindTestFiles(workspace.WorktreePath, tracked).Order(StringComparer.Ordinal).ToList(),
            time.GetUtcNow());
        cache.Save(baseline, target.TestArgs);
        progress.BaselineReady(baseline, fromCache: false);
        return baseline;
    }
}
