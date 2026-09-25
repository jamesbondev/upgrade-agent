using System.Globalization;
using ReadmeChecker.Agent;
using ReadmeChecker.Config;
using ReadmeChecker.Detection;
using ReadmeChecker.Reporting;
using RepoKit;
using RepoKit.AzureDevOps;

namespace ReadmeChecker.Run;

internal sealed class CheckOrchestrator(
    ResolvedConfig config,
    GitCli git,
    AzureDevOpsCredentialProvider credentials,
    IReadmeAssessor assessor,
    ICheckProgress progress,
    TimeProvider time)
{
    private ReadmeCheckerOptions Options => config.Options;

    public async Task<CheckReport> RunAsync(CheckArguments arguments, CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        var startedUtc = time.GetUtcNow();
        var runId = startedUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var outputDirectory = Path.Combine(config.OutputDirectory, $"run-{runId}");
        var workRoot = Path.Combine(config.WorkRoot, runId);
        var targets = Select(arguments.Only);

        var header = targets.Any(t => t.AzureDevOps is not null)
            ? (await credentials.AcquireAsync(cancellationToken)).AuthorizationHeader
            : null;

        var reports = new List<RepoReport>();
        var credits = 0.0;
        try
        {
            for (var i = 0; i < targets.Count; i++)
            {
                progress.RepoStarted(targets[i], i + 1, targets.Count);
                var capReached = Options.Agent.MaxAiCreditsPerRun > 0 && credits >= Options.Agent.MaxAiCreditsPerRun;
                var agentNote = arguments.Provider == AgentProvider.None ? "agent turned off"
                    : capReached ? $"AI credit cap of {Options.Agent.MaxAiCreditsPerRun} reached"
                    : null;

                var report = await CheckRepoAsync(targets[i], header, workRoot, outputDirectory, agentNote, cancellationToken);
                credits += report.Stats?.AiCredits ?? 0;
                reports.Add(report);
                await ReportWriter.WriteRepoAsync(report, outputDirectory, cancellationToken);
                progress.RepoFinished(report);
            }
        }
        finally
        {
            TryDeleteEmpty(workRoot);
        }

        var check = new CheckReport(runId, startedUtc, time.GetElapsedTime(started), outputDirectory, credits, reports);
        var reportPath = await ReportWriter.WriteAsync(check, cancellationToken);
        progress.RunFinished(check, reportPath);
        return check;
    }

    private List<RepoTarget> Select(IReadOnlyCollection<string> only)
    {
        if (only.Count == 0)
        {
            return [.. config.Repos];
        }

        var unknown = only.Where(o => !config.Repos.Any(r => r.Name.Equals(o, StringComparison.OrdinalIgnoreCase))).ToList();
        return unknown.Count > 0
            ? throw new ConfigurationException($"--only names repos that aren't configured: {string.Join(", ", unknown)}")
            : config.Repos.Where(r => only.Contains(r.Name, StringComparer.OrdinalIgnoreCase)).ToList();
    }

    private async Task<RepoReport> CheckRepoAsync(
        RepoTarget target, string? header, string workRoot, string outputDirectory, string? agentNote, CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        try
        {
            var report = await InspectAsync(target, header, workRoot, outputDirectory, agentNote, cancellationToken);
            return report with { Duration = time.GetElapsedTime(started) };
        }
        catch (AgentUnavailableException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var reason = ex is CloneException clone ? clone.Reason : ex.Message;
            return RepoReport.For(target, RepoVerdict.Error, reason) with { Duration = time.GetElapsedTime(started) };
        }
    }

    private async Task<RepoReport> InspectAsync(
        RepoTarget target, string? header, string workRoot, string outputDirectory, string? agentNote, CancellationToken cancellationToken)
    {
        var source = target.AzureDevOps is { } azureDevOps
            ? RepoSource.Remote(target.Name, azureDevOps.CloneUrl, header)
            : RepoSource.Local(target.Name, target.LocalPath!);

        await using var workspace = await RepoWorkspace.CloneAsync(
            source, workRoot, git, TimeSpan.FromMinutes(Options.Output.CloneTimeoutMinutes), cancellationToken);
        workspace.Keep = Options.Output.KeepClones;

        var facts = await RepoFacts.CollectAsync(workspace.Path, workspace.Git, target.ReadmePath, cancellationToken);
        if (facts.Readme is null)
        {
            var note = target.ReadmePath is null ? "no README at the repository root" : $"{target.ReadmePath} is not in the repository";
            return RepoReport.For(target, RepoVerdict.Missing, note) with { Deterministic = true };
        }

        var report = RepoReport.For(target, RepoVerdict.Unassessed) with
        {
            ReadmePath = facts.Readme.Path,
            ReadmeLastChanged = facts.Age?.LastChange.Date,
            CommitsSinceReadme = facts.Age?.CommitsSince,
        };

        if (facts.Readme.IsSymlink)
        {
            return report with { Verdict = RepoVerdict.Error, Note = "the README is a symbolic link, so it wasn't checked" };
        }

        var scan = (await IgnoredPaths.DropIgnoredAsync(ReadmeSignals.Find(facts), workspace.Path, workspace.Git, cancellationToken))
            .Capped(Options.Readme.MaxCandidates);
        report = report with { Signals = scan.Signals, SignalsNotListed = scan.Dropped };

        var recent = facts.Age is { } age && age.CommitsSince <= Options.Readme.RecentCommits;
        if (Options.Agent.SkipWhenClean && scan.Signals.Count == 0 && recent)
        {
            return report with
            {
                Verdict = RepoVerdict.Current,
                Deterministic = true,
                Note = $"no signals, and the README changed within the last {Options.Readme.RecentCommits} commits",
            };
        }

        if (agentNote is not null)
        {
            return scan.Signals.Any(s => s.Definitive)
                ? report with { Verdict = RepoVerdict.Stale, Deterministic = true, Note = $"broken links found; {agentNote}" }
                : report with { Note = agentNote };
        }

        var logPath = Path.Combine(outputDirectory, ReportWriter.FolderName(target.Name), "agent.log");
        var outcome = await assessor.AssessAsync(target.Name, workspace.Path, facts, scan, logPath, cancellationToken);
        return Combine(report, scan, outcome);
    }

    internal static RepoReport Combine(RepoReport report, SignalScan scan, AssessmentOutcome outcome)
    {
        var definitive = scan.Signals.Any(s => s.Definitive);
        var withAgent = report with
        {
            Issues = outcome.Issues,
            Unsupported = outcome.Rejected,
            AgentSummary = outcome.Summary,
            Stats = outcome.Stats,
        };

        return (outcome.Failure, outcome.Verdict, outcome.Issues.Count, definitive) switch
        {
            ({ } failure, _, _, true) => withAgent with { Verdict = RepoVerdict.Stale, Note = $"broken links found; {failure}" },
            ({ } failure, _, _, false) => withAgent with { Verdict = RepoVerdict.Unsure, Note = failure },
            (_, AssessedVerdict.Stale, > 0, _) => withAgent with { Verdict = RepoVerdict.Stale },
            (_, AssessedVerdict.Stale, 0, true) => withAgent with { Verdict = RepoVerdict.Stale, Note = "the agent's issues couldn't be verified, but broken links were found" },
            (_, AssessedVerdict.Stale, 0, false) => withAgent with { Verdict = RepoVerdict.Unsure, Note = "the agent's issues couldn't be verified against the README and repository" },
            (_, AssessedVerdict.Current, _, true) => withAgent with { Verdict = RepoVerdict.Stale, Note = "the agent found nothing, but broken links were found" },
            (_, AssessedVerdict.Current, _, false) => withAgent with { Verdict = RepoVerdict.Current },
            (_, _, _, true) => withAgent with { Verdict = RepoVerdict.Stale, Note = "the agent was unsure, but broken links were found" },
            _ => withAgent with { Verdict = RepoVerdict.Unsure },
        };
    }

    private static void TryDeleteEmpty(string folder)
    {
        try
        {
            if (Directory.Exists(folder) && !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder);
            }
        }
        catch (IOException)
        {
        }
    }
}
