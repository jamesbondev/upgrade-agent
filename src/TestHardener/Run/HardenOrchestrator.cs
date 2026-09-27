using System.Globalization;
using RepoKit;
using TestHardener.Config;
using TestHardener.Hardening;
using TestHardener.Infrastructure;
using TestHardener.Reporting;

namespace TestHardener.Run;

internal sealed class HardenOrchestrator(
    ResolvedConfig config,
    RepoSurveyor surveyor,
    IAgentBackendFactory backends,
    IGroupHardener hardener,
    DotnetCli dotnet,
    IHardenProgress progress,
    TimeProvider time)
{
    public async Task<HardenReport> RunAsync(HardenArguments arguments, CancellationToken cancellationToken)
    {
        var run = await surveyor.PrepareAsync(arguments.Only, arguments.From, cancellationToken);
        var repos = new List<RepoHardenReport>();
        var credits = 0.0;
        try
        {
            for (var i = 0; i < run.Targets.Count; i++)
            {
                var target = run.Targets[i];
                progress.RepoStarted(target, i + 1, run.Targets.Count);
                var started = time.GetTimestamp();
                await using var surveyed = await surveyor.SurveyAsync(target, run, cancellationToken);
                RepoHardenReport report;
                try
                {
                    report = await HardenRepoAsync(target, run, surveyed, credits, cancellationToken);
                }
                catch (Exception ex) when (ex is not (AgentUnavailableException or OperationCanceledException))
                {
                    report = new RepoHardenReport
                    {
                        Name = target.Name, Location = target.Location, Status = HardenStatus.Failed, Sha = surveyed.Survey.Sha, Note = ex.GetBaseException().Message,
                    };
                }

                report = report with { Duration = time.GetElapsedTime(started) };
                credits += report.AiCredits;
                repos.Add(report);
                await HardenReportWriter.WriteRepoAsync(report, run.RepoOutput(target.Name), cancellationToken);
                progress.RepoFinished(report);
            }
        }
        finally
        {
            RepoSurveyor.TryDeleteEmpty(run.WorkRoot);
        }

        var hardenReport = new HardenReport(run.RunId, run.StartedUtc, time.GetElapsedTime(run.StartedTimestamp), run.OutputDirectory, arguments.DryRun, credits, repos);
        var reportPath = await HardenReportWriter.WriteAsync(hardenReport, cancellationToken);
        progress.RunFinished(hardenReport, reportPath);
        return hardenReport;
    }

    internal static IReadOnlyList<PlannedGroup> Plan(RepoTarget target, RepoSurvey survey, int maxGroups) =>
        survey.Targets
            .Where(t => t.Failure is null)
            .SelectMany(t => t.Groups.Select(g => new PlannedGroup(target.Targets.First(c => c.Name == t.Name), g)))
            .OrderByDescending(p => p.Group.LogicSurvivors)
            .ThenByDescending(p => p.Group.Survivors.Count)
            .ThenByDescending(p => p.Group.FixCommits)
            .ThenBy(p => p.Group.File, StringComparer.Ordinal)
            .ThenBy(p => p.Group.Member.StartLine)
            .Take(maxGroups)
            .ToList();

    private async Task<RepoHardenReport> HardenRepoAsync(
        RepoTarget target, RunContext run, SurveyedRepo surveyed, double creditsSoFar, CancellationToken cancellationToken)
    {
        var survey = surveyed.Survey;
        var report = new RepoHardenReport { Name = target.Name, Location = target.Location, Status = HardenStatus.Failed, Sha = survey.Sha };
        if (surveyed.Workspace is not { } workspace || survey.Status == SurveyStatus.Failed)
        {
            return report with { Note = survey.Note ?? "the survey failed" };
        }

        var planned = Plan(target, survey, config.Options.Hardening.MaxGroupsPerRun);
        if (planned.Count == 0)
        {
            return report with { Status = HardenStatus.NothingToDo, Note = "no surviving mutants worth a test" };
        }

        await using var backend = backends.Create();
        await backends.EnsureReadyAsync(backend, workspace.Path, cancellationToken);
        var tracked = await workspace.Git.ListFilesAsync(workspace.Path, cancellationToken);
        var runner = TestRunnerDetector.Detect(workspace.Path);
        var kept = new Dictionary<string, string>(StringComparer.Ordinal);
        var results = new List<GroupResult>();
        var credits = 0.0;
        var repoOutput = run.RepoOutput(target.Name);

        for (var i = 0; i < planned.Count; i++)
        {
            var (targetConfig, group) = planned[i];
            var cap = config.Options.Agent.MaxAiCreditsPerRun;
            if (cap > 0 && creditsSoFar + credits >= cap)
            {
                results.Add(new GroupResult
                {
                    Number = i + 1, Target = targetConfig.Name, Group = group, Outcome = GroupOutcome.Skipped,
                    Reason = string.Create(CultureInfo.InvariantCulture, $"the AI credit cap of {cap} was reached"),
                });
                continue;
            }

            progress.GroupStarted(i + 1, planned.Count, planned[i]);
            var groupOutput = Path.Combine(repoOutput, "groups", (i + 1).ToString(CultureInfo.InvariantCulture));
            var job = new GroupJob(
                i + 1, target.Name, workspace.Path, workspace.Git, targetConfig, runner, group, tracked, kept,
                target.ConventionFiles, target.TestNamePattern, groupOutput, run.Environment);
            var result = await hardener.HardenAsync(job, backend, cancellationToken);
            credits += result.Stats?.AiCredits ?? 0;
            if (result.Outcome == GroupOutcome.Verified && result.Owned is { } owned)
            {
                kept[owned.Path] = await File.ReadAllTextAsync(Path.Combine(workspace.Path, owned.Path), cancellationToken);
            }

            results.Add(result);
            await HardenReportWriter.WriteGroupAsync(result, groupOutput, cancellationToken);
            progress.GroupFinished(result);
        }

        report = report with { Groups = results, AiCredits = credits, ChangedFiles = [.. kept.Keys.Order(StringComparer.Ordinal)] };
        if (kept.Count == 0)
        {
            return report with { Status = HardenStatus.Rejected, Note = "no group passed the checks" };
        }

        var patchPath = await WritePatchAsync(workspace, kept.Keys, repoOutput, cancellationToken);
        report = report with { PatchPath = patchPath };
        return await FinalCheckAsync(target, workspace.Path, run, repoOutput, cancellationToken) is { } failure
            ? report with { Status = HardenStatus.Rejected, Note = failure }
            : report with { Status = HardenStatus.Ready };
    }

    private async Task<string?> FinalCheckAsync(RepoTarget target, string root, RunContext run, string repoOutput, CancellationToken cancellationToken)
    {
        foreach (var project in target.VerifyTestProjects)
        {
            var build = await dotnet.BuildAsync(root, project, run.Environment, cancellationToken);
            if (!build.Succeeded)
            {
                return $"{project} doesn't build with the new tests: {string.Join("; ", build.Errors.Take(5))}";
            }

            var results = Path.Combine(repoOutput, "final", Path.GetFileNameWithoutExtension(project));
            var outcome = await dotnet.TestAsync(root, project, null, results, run.Environment, cancellationToken);
            if (!outcome.Succeeded)
            {
                var failures = outcome.Failures.Count == 0 ? "no tests ran" : string.Join("; ", outcome.Failures.Take(5).Select(f => f.Test));
                return $"{project} fails with the new tests: {failures}";
            }
        }

        return null;
    }

    private static async Task<string> WritePatchAsync(RepoWorkspace workspace, IEnumerable<string> files, string repoOutput, CancellationToken cancellationToken)
    {
        var paths = files.Order(StringComparer.Ordinal).ToList();
        await workspace.Git.RunAsync(workspace.Path, ["add", "--intent-to-add", "--", .. paths], cancellationToken);
        var diff = await workspace.Git.RunAsync(workspace.Path, ["diff", "--", .. paths], cancellationToken);
        Directory.CreateDirectory(repoOutput);
        var patchPath = Path.Combine(repoOutput, "hardening.patch");
        await File.WriteAllTextAsync(patchPath, diff, cancellationToken);
        return patchPath;
    }
}
