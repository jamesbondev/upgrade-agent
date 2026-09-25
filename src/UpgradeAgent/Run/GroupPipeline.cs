using UpgradeAgent.Build;
using UpgradeAgent.Bumping;
using UpgradeAgent.Config;
using UpgradeAgent.Detection;
using UpgradeAgent.Guardrails;
using UpgradeAgent.Infrastructure;
using UpgradeAgent.Workspace;

namespace UpgradeAgent.Run;

internal sealed record GroupRun(RunWorkspace Workspace, Baseline Baseline, TestRunnerMode RunnerMode, bool ForceEvaluate, IGroupFixer Fixer);

internal sealed class GroupPipeline(
    GitCli git,
    DotnetCli dotnet,
    GuardrailRunner guardrails,
    GroupCommitter committer,
    TargetOptions target,
    AgentOptions agent,
    IRunProgress progress,
    TimeProvider time)
{
    private static readonly string[] IncompatibleFrameworkCodes = ["NU1201", "NU1202", "NU1203"];

    public async Task<GroupResult> RunAsync(GroupRun run, UpdateGroup group, CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        var worktree = run.Workspace.WorktreePath;
        var start = await git.HeadAsync(worktree, cancellationToken);
        BumpResult? bump = null;
        GuardrailReport? report = null;
        FixOutcome? fix = null;

        GroupResult Result(GroupStatus status, string? reason, string? commit = null) =>
            new(group.Name, group.Kind, status, reason, commit, bump?.Edits ?? [], bump?.Manual ?? [], report, fix, time.GetElapsedTime(started));

        async Task<GroupResult> RejectAsync(string reason, GroupStatus status = GroupStatus.Rejected)
        {
            await git.RevertToAsync(worktree, start);
            return Result(status, reason);
        }

        try
        {
            bump = VersionBumper.Apply(worktree, group.Updates);
            progress.Bumped(bump);
            if (bump.Edits.Count == 0)
            {
                return await RejectAsync("no version entries to change", GroupStatus.NothingToDo);
            }

            var restore = await dotnet.RestoreAsync(run.Workspace.SolutionPath, run.ForceEvaluate, cancellationToken);
            if (!restore.Succeeded)
            {
                progress.Built("Restore", restore);
                return await RejectAsync(restore.Errors.Any(e => IncompatibleFrameworkCodes.Contains(e.Code))
                    ? "needs TFM upgrade (restore: package not compatible with project framework)"
                    : "restore failed");
            }

            var startState = await guardrails.CaptureStartAsync(worktree, start, cancellationToken);
            var label = RepoPath.SafeFileName(group.Name);
            var (build, tests) = await BuildAndTestAsync(run, $"{label}-after-bump", cancellationToken);

            if (AgentGate.TooLargeForAgent(build, agent.MaxErrorsForAgent) is { } tooLarge)
            {
                return await RejectAsync(tooLarge);
            }

            if (!build.Succeeded || tests is not { Succeeded: true })
            {
                fix = await run.Fixer.FixAsync(
                    new FixContext(worktree, run.Workspace.SolutionPath, run.Workspace.OutputDirectory, group, build, tests, target.TestArgs), cancellationToken);
                progress.Fixed(fix);
                if (fix.Attempted)
                {
                    (build, tests) = await BuildAndTestAsync(run, $"{label}-after-fix", cancellationToken);
                }
            }

            var review = new ReviewContext(
                fix?.Details?.Packages.SelectMany(p => p.Fixes).Select(f => f.File).ToList(),
                bump.Edits.Select(e => e.File).Distinct().ToList());
            report = await guardrails.RunAsync(new GuardrailInput(worktree, startState, run.Baseline, build, tests, review), cancellationToken);
            progress.GuardrailsChecked(report);
            if (!report.Passed)
            {
                return await RejectAsync(fix is { Attempted: false } ? $"{report.FailureSummary} ({fix.Summary})" : report.FailureSummary);
            }

            var message = CommitMessage.Create(group.Name, bump.Edits, run.Workspace.RunId);
            return await committer.CommitAsync(worktree, message, cancellationToken) switch
            {
                CommitResult.Committed committed => Result(GroupStatus.Accepted, null, committed.Sha) with { BuildWarnings = WarningSummary(build) },
                CommitResult.Refused refused => await RejectAsync(refused.Reason),
                _ => throw new InvalidOperationException("Unknown commit result."),
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await RejectAsync("cancelled; changes reverted", GroupStatus.Cancelled);
        }
    }

    private async Task<(BuildResult Build, TestRunResult? Tests)> BuildAndTestAsync(GroupRun run, string label, CancellationToken cancellationToken)
    {
        var build = await dotnet.BuildAsync(run.Workspace.SolutionPath, cancellationToken);
        progress.Built("Build", build);
        if (!build.Succeeded)
        {
            return (build, null);
        }

        var tests = await dotnet.TestAsync(
            run.Workspace.SolutionPath, Path.Combine(run.Workspace.OutputDirectory, "trx", label), run.RunnerMode, target.TestArgs, cancellationToken);
        progress.Tested(tests);
        return (build, tests);
    }

    private static IReadOnlyList<string> WarningSummary(BuildResult build) =>
        build.Warnings
            .GroupBy(w => (w.Code, w.Message))
            .OrderBy(g => g.Key.Code, StringComparer.Ordinal)
            .ThenByDescending(g => g.Count())
            .Select(g => $"{g.Key.Code} ×{g.Count()}: {g.Key.Message}")
            .ToList();
}
