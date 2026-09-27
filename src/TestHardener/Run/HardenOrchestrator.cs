using System.Globalization;
using RepoKit;
using RepoKit.AzureDevOps;
using TestHardener.Analysis;
using TestHardener.Config;
using TestHardener.Hardening;
using TestHardener.Infrastructure;
using TestHardener.Publishing;
using TestHardener.Reporting;
using TestHardener.Stryker;

namespace TestHardener.Run;

internal sealed class HardenOrchestrator(
    ResolvedConfig config,
    RepoSurveyor surveyor,
    IAgentBackendFactory backends,
    IGroupHardener hardener,
    DotnetCli dotnet,
    IPullRequestHosts hosts,
    IHardenProgress progress,
    TimeProvider time)
{
    private PublishOptions Publish => config.Options.Publish;

    public async Task<HardenReport> RunAsync(HardenArguments arguments, CancellationToken cancellationToken)
    {
        var run = await surveyor.PrepareAsync(arguments.Only, arguments.From, cancellationToken);
        var repos = new List<RepoHardenReport>();
        var credits = 0.0;
        AgentUnavailableException? stop = null;
        try
        {
            for (var i = 0; i < run.Targets.Count && stop is null; i++)
            {
                var target = run.Targets[i];
                progress.RepoStarted(target, i + 1, run.Targets.Count);
                var started = time.GetTimestamp();
                var outcome = await HardenRepoAsync(target, run, arguments, credits, cancellationToken);
                var report = outcome.Report with { Duration = time.GetElapsedTime(started) };
                stop = outcome.Stop;
                credits += report.AiCredits;
                repos.Add(report);
                await TryWriteAsync(() => HardenReportWriter.WriteRepoAsync(report, run.RepoOutput(target.Name), cancellationToken));
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
        return stop is null ? hardenReport : throw stop;
    }

    internal static IReadOnlyList<PlannedGroup> Plan(RepoTarget target, RepoSurvey survey, int maxGroups) =>
        GroupRanking.Order(
                survey.Targets
                    .Where(t => t.Failure is null)
                    .SelectMany(t => t.Groups.Select(g => new PlannedGroup(target.Targets.First(c => c.Name == t.Name), g))),
                p => p.Group)
            .Take(maxGroups)
            .ToList();

    private async Task<(RepoHardenReport Report, AgentUnavailableException? Stop)> HardenRepoAsync(
        RepoTarget target, RunContext run, HardenArguments arguments, double creditsSoFar, CancellationToken cancellationToken)
    {
        var report = new RepoHardenReport { Name = target.Name, Location = target.Location, Status = HardenStatus.Failed };
        var host = hosts.For(target, run.Credential);
        try
        {
            var previous = host is null ? [] : await host.ListAsync(Publish.BranchPrefix, cancellationToken);
            if (host is not null && PreviousPullRequest(previous) is { } skip)
            {
                return (report with { Status = HardenStatus.Skipped, Note = skip }, null);
            }

            await using var surveyed = await surveyor.SurveyAsync(target, run, cancellationToken);
            report = report with { Sha = surveyed.Survey.Sha };
            if (surveyed.Workspace is not { } workspace || surveyed.Survey.Status == SurveyStatus.Failed)
            {
                return (report with { Note = surveyed.Survey.Note ?? "the survey failed" }, null);
            }

            if (TestRunnerDetector.Detect(workspace.Path) == TestRunnerMode.TestingPlatform)
            {
                return (report with
                {
                    Note = "this repo uses Microsoft.Testing.Platform, where Stryker ignores test filters, so kills can't be checked per test",
                }, null);
            }

            if (host is not null)
            {
                report = report with { LeftoverBranches = await LeftoverBranchesAsync(previous, workspace, cancellationToken) };
            }

            return await HardenWorkspaceAsync(target, run, arguments, surveyed.Survey, workspace, host, report, creditsSoFar, cancellationToken);
        }
        catch (Exception ex) when (ex is not AgentUnavailableException && (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested))
        {
            return (report with { Status = HardenStatus.Failed, Note = ex.GetBaseException().Message }, null);
        }
    }

    private async Task<(RepoHardenReport Report, AgentUnavailableException? Stop)> HardenWorkspaceAsync(
        RepoTarget target,
        RunContext run,
        HardenArguments arguments,
        RepoSurvey survey,
        RepoWorkspace workspace,
        IPullRequestHost? host,
        RepoHardenReport report,
        double creditsSoFar,
        CancellationToken cancellationToken)
    {
        var planned = Plan(target, survey, config.Options.Hardening.MaxGroupsPerRun);
        if (planned.Count == 0)
        {
            return (report with { Status = HardenStatus.NothingToDo, Note = "no surviving mutants worth a test" }, null);
        }

        var branch = Publish.BranchPrefix + run.RunId;
        await workspace.Git.CreateBranchAsync(workspace.Path, branch, cancellationToken);
        report = report with { Branch = branch };

        var groups = await HardenGroupsAsync(target, run, workspace, planned, creditsSoFar, cancellationToken);
        report = report with { Groups = groups.Results, AiCredits = groups.Credits, ChangedFiles = [.. groups.Kept.Keys.Order(StringComparer.Ordinal)] };
        var stopNote = groups.Stop is null ? "" : $"; Copilot stopped the run after {groups.Results.Count} groups: {groups.Stop.Message}";
        if (groups.Kept.Count == 0)
        {
            return groups.Stop is { } stop && groups.Results.All(r => r.Outcome != GroupOutcome.Rejected)
                ? (report with { Status = HardenStatus.Failed, Note = $"Copilot stopped the run before any group passed: {stop.Message}" }, stop)
                : (report with { Status = HardenStatus.Rejected, Note = "no group passed the checks" + stopNote }, groups.Stop);
        }

        var repoOutput = run.RepoOutput(target.Name);
        report = report with { PatchPath = await WritePatchAsync(workspace, groups.Kept.Keys, repoOutput, cancellationToken) };
        if (await FinalCheckAsync(target, workspace.Path, run, repoOutput, cancellationToken) is { } failure)
        {
            return (report with { Status = HardenStatus.Rejected, Note = failure + stopNote }, groups.Stop);
        }

        if (arguments.DryRun || host is null)
        {
            var why = arguments.DryRun ? "dry run: the tests are saved as a patch, nothing was pushed" : "local repo: the tests are saved as a patch, not pushed";
            return (report with { Status = HardenStatus.Ready, Note = why + stopNote }, groups.Stop);
        }

        var verified = groups.Results.Where(r => r.Outcome == GroupOutcome.Verified).ToList();
        var approved = await arguments.Prompter.ConfirmAsync(
            $"push {branch} to {target.Name} and open a draft pull request",
            $"{report.TestsAdded} tests passed every check and catch {report.Killed} surviving mutants",
            cancellationToken);
        if (!approved)
        {
            return (report with { Status = HardenStatus.Declined, Note = "not approved; the tests are saved as a patch" + stopNote }, groups.Stop);
        }

        return (await PublishAsync(target, run, workspace, host, verified, report, cancellationToken), groups.Stop);
    }

    private async Task<(List<GroupResult> Results, Dictionary<string, string> Kept, double Credits, AgentUnavailableException? Stop)> HardenGroupsAsync(
        RepoTarget target, RunContext run, RepoWorkspace workspace, IReadOnlyList<PlannedGroup> planned, double creditsSoFar, CancellationToken cancellationToken)
    {
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
                target.ConventionFiles, target.TestNamePattern, groupOutput, run.Environment,
                cap > 0 ? cap - creditsSoFar - credits : null);
            GroupResult result;
            try
            {
                result = await hardener.HardenAsync(job, backend, cancellationToken);
            }
            catch (AgentUnavailableException ex)
            {
                return (results, kept, credits, ex);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                result = new GroupResult { Number = i + 1, Target = targetConfig.Name, Group = group, Outcome = GroupOutcome.Failed, Reason = ex.GetBaseException().Message };
            }

            credits += result.Stats?.AiCredits ?? 0;
            if (result.Outcome == GroupOutcome.Verified && result.Owned is { } owned)
            {
                kept[owned.Path] = await File.ReadAllTextAsync(Path.Combine(workspace.Path, owned.Path), cancellationToken);
            }

            results.Add(result);
            await TryWriteAsync(() => HardenReportWriter.WriteGroupAsync(result, groupOutput, cancellationToken));
            progress.GroupFinished(result);
        }

        return (results, kept, credits, null);
    }

    private async Task<RepoHardenReport> PublishAsync(
        RepoTarget target, RunContext run, RepoWorkspace workspace, IPullRequestHost host, IReadOnlyList<GroupResult> verified, RepoHardenReport report, CancellationToken cancellationToken)
    {
        var identity = new GitIdentity(Publish.CommitName, Publish.CommitEmail);
        var scopes = verified.Select(g => target.Targets.FirstOrDefault(t => t.Name == g.Target)?.CommitScope).ToList();
        await workspace.Git.CommitPathsAsync(workspace.Path, report.ChangedFiles, PullRequestText.CommitMessage(verified, scopes), identity, cancellationToken: cancellationToken);
        var targetBranch = await workspace.Git.DefaultBranchAsync(workspace.Path, cancellationToken);
        await workspace.Git.PushAsync(workspace.Path, workspace.Source.Location, report.Branch!, cancellationToken);

        try
        {
            var scores = FileScore.From(await ReportsAsync(run, target, cancellationToken), verified);
            var description = PullRequestText.Description(report.Sha ?? "", verified, scores, config.Options.Hardening.OriginalRuns);
            var pullRequest = await host.CreateDraftAsync(report.Branch!, targetBranch, PullRequestText.Title(verified), description, cancellationToken);
            return report with { Status = HardenStatus.Opened, PullRequestUrl = pullRequest.WebUrl };
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return report with { Status = HardenStatus.Failed, Note = $"pushed {report.Branch}, but the pull request couldn't be opened: {ex.Message}" };
        }
    }

    private string? PreviousPullRequest(IReadOnlyList<AzureDevOpsPullRequest> previous)
    {
        if (previous.FirstOrDefault(p => p.Status.Equals("active", StringComparison.OrdinalIgnoreCase)) is { } open)
        {
            return $"a pull request is already open: {open.WebUrl}";
        }

        var since = time.GetUtcNow().AddDays(-Publish.CooldownDays);
        return previous.Where(p => p.Created >= since).OrderByDescending(p => p.Created).FirstOrDefault() is { } recent
            ? string.Create(CultureInfo.InvariantCulture, $"a pull request was opened on {recent.Created:yyyy-MM-dd}, within the last {Publish.CooldownDays} days: {recent.WebUrl}")
            : null;
    }

    private async Task<IReadOnlyList<string>> LeftoverBranchesAsync(
        IReadOnlyList<AzureDevOpsPullRequest> previous, RepoWorkspace workspace, CancellationToken cancellationToken)
    {
        var remote = await workspace.Git.TryRunAsync(workspace.Path, ["ls-remote", "--heads", "origin", $"refs/heads/{Publish.BranchPrefix}*"], cancellationToken);
        if (!remote.Succeeded)
        {
            return [];
        }

        var withPullRequests = previous.Select(p => p.SourceBranch.Replace("refs/heads/", "", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        return remote.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split('\t').Last().Replace("refs/heads/", "", StringComparison.Ordinal))
            .Where(branch => !withPullRequests.Contains(branch))
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static async Task<IReadOnlyList<MutationReport>> ReportsAsync(RunContext run, RepoTarget target, CancellationToken cancellationToken)
    {
        var reports = new List<MutationReport>();
        foreach (var targetConfig in target.Targets)
        {
            var path = Path.Combine(run.RepoOutput(target.Name), "stryker", targetConfig.Name, StrykerRunner.ReportRelativePath);
            if (File.Exists(path))
            {
                try
                {
                    reports.Add(MutationReportParser.Parse(await File.ReadAllTextAsync(path, cancellationToken), ""));
                }
                catch (MutationReportException)
                {
                }
            }
        }

        return reports;
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
            var timeout = TimeSpan.FromMinutes(config.Options.Hardening.TestTimeoutMinutes * 3);
            var outcome = await dotnet.TestAsync(root, project, null, results, run.Environment, timeout, cancellationToken);
            if (!outcome.Succeeded)
            {
                var failures = outcome.Failures.Count == 0 ? "no tests passed" : string.Join("; ", outcome.Failures.Take(5).Select(f => f.Test));
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

    private static async Task TryWriteAsync(Func<Task> write)
    {
        try
        {
            await write();
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
