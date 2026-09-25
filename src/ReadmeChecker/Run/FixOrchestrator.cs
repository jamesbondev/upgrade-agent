using System.Globalization;
using ReadmeChecker.Agent;
using ReadmeChecker.Config;
using ReadmeChecker.Fixing;
using ReadmeChecker.Publishing;
using ReadmeChecker.Reporting;
using RepoKit;

namespace ReadmeChecker.Run;

internal sealed class FixOrchestrator(
    ResolvedConfig config,
    RepoInspector inspector,
    IReadmeFixer fixer,
    IPullRequestHosts hosts,
    IFixProgress progress,
    TimeProvider time)
{
    private ReadmeCheckerOptions Options => config.Options;

    public async Task<FixRunReport> RunAsync(FixArguments arguments, CancellationToken cancellationToken)
    {
        var run = await inspector.PrepareAsync(arguments.Only, cancellationToken);
        var reports = new List<RepoFixReport>();
        var credits = 0.0;
        try
        {
            for (var i = 0; i < run.Targets.Count; i++)
            {
                progress.RepoStarted(run.Targets[i], i + 1, run.Targets.Count);
                var report = await FixRepoAsync(run.Targets[i], run, arguments, credits, cancellationToken);
                credits += report.AiCredits;
                reports.Add(report);
                await FixReportWriter.WriteRepoAsync(report, run.OutputDirectory, cancellationToken);
                progress.RepoFinished(report);
            }
        }
        finally
        {
            RepoInspector.TryDeleteEmpty(run.WorkRoot);
        }

        var fix = new FixRunReport(run.RunId, run.StartedUtc, time.GetElapsedTime(run.StartedTimestamp), run.OutputDirectory, credits, reports);
        var reportPath = await FixReportWriter.WriteAsync(fix, cancellationToken);
        progress.RunFinished(fix, reportPath);
        return fix;
    }

    private async Task<RepoFixReport> FixRepoAsync(RepoTarget target, RunContext run, FixArguments arguments, double credits, CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        await using var inspection = await inspector.InspectAsync(target, run, inspector.AgentNote(AgentProvider.Copilot, credits), cancellationToken);
        var report = new RepoFixReport { Name = target.Name, Location = target.Location, Status = FixStatus.NothingToFix, Check = inspection.Report };
        try
        {
            report = await ActAsync(target, run, arguments, inspection, report, cancellationToken);
        }
        catch (AgentUnavailableException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            report = report with { Status = FixStatus.Failed, Reason = ex.GetBaseException().Message };
        }

        return report with { Duration = time.GetElapsedTime(started) };
    }

    private async Task<RepoFixReport> ActAsync(
        RepoTarget target, RunContext run, FixArguments arguments, Inspection inspection, RepoFixReport report, CancellationToken cancellationToken)
    {
        var check = inspection.Report;
        if (check.Verdict != RepoVerdict.Stale || inspection.Workspace is not { } workspace || inspection.Facts is not { Readme: { } readme } facts)
        {
            return report with { Reason = check.Note is null ? check.Verdict.ToString() : $"{check.Verdict}: {check.Note}" };
        }

        var certain = check.Signals.Where(s => s.Definitive).ToList();
        if (check.Issues.Count == 0 && certain.Count == 0)
        {
            return report with { Reason = "stale, but with no specific problem to fix" };
        }

        var host = hosts.For(target, run.Credential);
        if (host is not null && await PreviousPullRequestAsync(host, cancellationToken) is { } previous)
        {
            return report with { Status = FixStatus.Skipped, Reason = previous };
        }

        var branch = Options.Publish.BranchPrefix + run.RunId;
        await workspace.Git.CreateBranchAsync(workspace.Path, branch, cancellationToken);
        var repoFolder = Path.Combine(run.OutputDirectory, ReportWriter.FolderName(target.Name));
        var attempt = await fixer.FixAsync(target.Name, workspace.Path, facts, check.Issues, certain, Path.Combine(repoFolder, "fix-agent.log"), cancellationToken);
        report = report with { Branch = branch, AgentSummary = attempt.Summary, FixStats = attempt.Stats };
        if (attempt.Failure is not null)
        {
            return report with { Status = FixStatus.Failed, Reason = attempt.Failure };
        }

        var verification = await ReadmeVerifier.VerifyAsync(workspace, facts, Options.Readme.MinKeptRatio, cancellationToken);
        if (verification.Diff.Length > 0)
        {
            Directory.CreateDirectory(repoFolder);
            var patchPath = Path.Combine(repoFolder, "readme.patch");
            await File.WriteAllTextAsync(patchPath, verification.Diff, cancellationToken);
            report = report with { PatchPath = patchPath };
        }

        if (!verification.Passed)
        {
            return report with { Status = FixStatus.Rejected, Reason = "the fix failed the checks", Problems = verification.Problems };
        }

        if (arguments.DryRun || host is null)
        {
            var why = arguments.DryRun ? "dry run: nothing was pushed" : "local repo: the fix is saved as a patch, not pushed";
            return report with { Status = FixStatus.Ready, Reason = why };
        }

        var approved = await arguments.Prompter.ConfirmAsync(
            $"push {branch} to {target.Name} and open a draft pull request", "the README fix passed the checks", cancellationToken);
        if (!approved)
        {
            return report with { Status = FixStatus.Declined, Reason = "not approved; the fix is saved as a patch" };
        }

        var identity = new GitIdentity(Options.Publish.CommitName, Options.Publish.CommitEmail);
        await workspace.Git.CommitPathsAsync(workspace.Path, [readme.Path], PullRequestText.CommitMessage(readme.Path, check.Issues, certain), identity, cancellationToken: cancellationToken);
        var targetBranch = await workspace.Git.DefaultBranchAsync(workspace.Path, cancellationToken);
        await workspace.Git.PushAsync(workspace.Path, workspace.Source.Location, branch, cancellationToken);

        try
        {
            var pullRequest = await host.CreateDraftAsync(
                branch, targetBranch, PullRequestText.Title, PullRequestText.Description(readme.Path, check.Issues, certain, attempt.Summary), cancellationToken);
            return report with { Status = FixStatus.Opened, PullRequestUrl = pullRequest.WebUrl };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return report with { Status = FixStatus.Failed, Reason = $"pushed {branch}, but the pull request couldn't be opened: {ex.Message}" };
        }
    }

    private async Task<string?> PreviousPullRequestAsync(IPullRequestHost host, CancellationToken cancellationToken)
    {
        var previous = await host.ListAsync(Options.Publish.BranchPrefix, cancellationToken);
        if (previous.FirstOrDefault(p => p.Status.Equals("active", StringComparison.OrdinalIgnoreCase)) is { } open)
        {
            return $"a pull request is already open: {open.WebUrl}";
        }

        var since = time.GetUtcNow().AddDays(-Options.Publish.CooldownDays);
        return previous.Where(p => p.Created >= since).OrderByDescending(p => p.Created).FirstOrDefault() is { } recent
            ? string.Create(CultureInfo.InvariantCulture, $"a pull request was opened on {recent.Created:yyyy-MM-dd}, within the last {Options.Publish.CooldownDays} days: {recent.WebUrl}")
            : null;
    }
}
