using System.Text.Json;
using UpgradeAgent.Build;
using UpgradeAgent.Config;
using UpgradeAgent.Detection;
using UpgradeAgent.Guardrails;
using UpgradeAgent.Infrastructure;
using UpgradeAgent.Workspace;

namespace UpgradeAgent.Run;

/// <summary>
/// The deterministic spine of a run: workspace, baseline, plan, then each group through the
/// <see cref="GroupPipeline"/>, then the report. The repo's own working tree and branches are never touched.
/// </summary>
internal sealed class RunOrchestrator(
    ResolvedConfig config,
    GitCli git,
    BaselineProvider baselines,
    PlanService plans,
    GroupPipeline pipeline,
    IRunProgress progress,
    TimeProvider time)
{
    public async Task<RunReport> RunAsync(PlanSource planSource, IReadOnlyCollection<string> only, IGroupFixer fixer, CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        var startedUtc = time.GetUtcNow();
        var targetCommit = await git.HeadAsync(config.RepoPath, cancellationToken);

        var workspace = await CreateWorkspaceAsync(startedUtc, cancellationToken);
        var forceEvaluate = Directory.EnumerateFiles(workspace.WorktreePath, "packages.lock.json", SearchOption.AllDirectories).Any();
        var runnerMode = TestRunnerDetector.Detect(workspace.WorktreePath);
        var baseline = await baselines.GetAsync(workspace, targetCommit, runnerMode, forceEvaluate, cancellationToken);

        var plan = (await LoadPlanAsync(planSource, workspace, cancellationToken)).Narrow(only, new PackageFamilies(config.Options.Policy.EffectiveGroups));
        progress.PlanReady(plan);
        await PlanStore.SaveAsync(plan, Path.Combine(workspace.OutputDirectory, "plan.json"), cancellationToken);

        var run = new GroupRun(workspace, baseline, runnerMode, forceEvaluate, fixer);
        var results = new List<GroupResult>();
        for (var i = 0; i < plan.Groups.Count; i++)
        {
            var group = plan.Groups[i];
            progress.GroupStarted(group, i + 1, plan.Groups.Count);
            var result = await pipeline.RunAsync(run, group, cancellationToken);
            results.Add(result);
            progress.GroupFinished(result);
            if (result.Status == GroupStatus.Cancelled)
            {
                break;
            }
        }

        var report = new RunReport(
            workspace.RunId, workspace.BranchName, workspace.WorktreePath, workspace.OutputDirectory, targetCommit, baseline.SdkVersion,
            startedUtc, time.GetElapsedTime(started), plan, results);
        await File.WriteAllTextAsync(Path.Combine(workspace.OutputDirectory, "run.json"), JsonSerializer.Serialize(report, JsonDefaults.Options), CancellationToken.None);

        var log = await git.RunAsync(workspace.WorktreePath, ["log", "--oneline", "--no-decorate", $"{targetCommit}..HEAD"], CancellationToken.None);
        progress.RunFinished(report, GitCli.SplitLines(log));
        return report;
    }

    /// <summary>Checks where the worktree would go before creating it, so an abort leaves nothing behind.</summary>
    private async Task<RunWorkspace> CreateWorkspaceAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var workspace = await RunWorkspace.PlanAsync(git, config.RepoPath, config.SolutionPath, config.WorkRoot, config.OutputDirectory, now, cancellationToken);
        var leaks = RunWorkspace.FindConfigLeaks(config.RepoPath, workspace.WorktreePath);
        if (leaks.Count > 0)
        {
            throw new RunAbortedException(
                $"The worktree would inherit config files the repo doesn't: {string.Join(", ", leaks)}. Set Target:WorkRoot to a folder beside the repo.");
        }

        await workspace.CreateAsync(git, cancellationToken);
        progress.WorkspaceReady(workspace);
        return workspace;
    }

    private async Task<UpgradePlan> LoadPlanAsync(PlanSource source, RunWorkspace workspace, CancellationToken cancellationToken) => source switch
    {
        PlanSource.Frozen frozen => frozen.Plan,
        PlanSource.FromFile file => PlanStore.Load(file.Path),
        _ => await progress.WithSpinnerAsync(
            "Detecting outdated packages (latest, highest minor, highest patch)...",
            () => plans.DetectAsync(workspace.WorktreePath, workspace.SolutionPath, cancellationToken)),
    };
}
