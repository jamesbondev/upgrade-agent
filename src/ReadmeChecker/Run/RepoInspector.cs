using System.Globalization;
using ReadmeChecker.Agent;
using ReadmeChecker.Config;
using ReadmeChecker.Detection;
using ReadmeChecker.Reporting;
using RepoKit;
using RepoKit.AzureDevOps;

namespace ReadmeChecker.Run;

internal sealed record RunContext(
    string RunId,
    DateTimeOffset StartedUtc,
    long StartedTimestamp,
    string OutputDirectory,
    string WorkRoot,
    IReadOnlyList<RepoTarget> Targets,
    AzureDevOpsCredential? Credential);

internal sealed class Inspection(RepoReport report, RepoWorkspace? workspace, RepoFacts? facts, SignalScan scan) : IAsyncDisposable
{
    public RepoReport Report { get; } = report;

    public RepoWorkspace? Workspace { get; } = workspace;

    public RepoFacts? Facts { get; } = facts;

    public SignalScan Scan { get; } = scan;

    public ValueTask DisposeAsync() => Workspace?.DisposeAsync() ?? ValueTask.CompletedTask;
}

internal sealed class RepoInspector(
    ResolvedConfig config,
    GitCli git,
    AzureDevOpsCredentialProvider credentials,
    IReadmeAssessor assessor,
    TimeProvider time)
{
    private ReadmeCheckerOptions Options => config.Options;

    public async Task<RunContext> PrepareAsync(IReadOnlyCollection<string> only, CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        var startedUtc = time.GetUtcNow();
        var runId = startedUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var targets = Select(only);
        var credential = targets.Any(t => t.AzureDevOps is not null) ? await credentials.AcquireAsync(cancellationToken) : null;
        return new RunContext(
            runId, startedUtc, started, Path.Combine(config.OutputDirectory, $"run-{runId}"), Path.Combine(config.WorkRoot, runId), targets, credential);
    }

    public string? AgentNote(AgentProvider provider, double creditsSoFar) =>
        provider == AgentProvider.None ? "agent turned off"
        : Options.Agent.MaxAiCreditsPerRun > 0 && creditsSoFar >= Options.Agent.MaxAiCreditsPerRun ? $"AI credit cap of {Options.Agent.MaxAiCreditsPerRun} reached"
        : null;

    public async Task<Inspection> InspectAsync(RepoTarget target, RunContext run, string? agentNote, CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        RepoWorkspace? workspace = null;
        try
        {
            var source = target.AzureDevOps is { } azureDevOps
                ? RepoSource.Remote(target.Name, azureDevOps.CloneUrl, run.Credential?.AuthorizationHeader)
                : RepoSource.Local(target.Name, target.LocalPath!);

            workspace = await RepoWorkspace.CloneAsync(
                source, run.WorkRoot, git, TimeSpan.FromMinutes(Options.Output.CloneTimeoutMinutes), cancellationToken);
            workspace.Keep = Options.Output.KeepClones;

            var (report, facts, scan) = await AssessAsync(target, workspace, run.OutputDirectory, agentNote, cancellationToken);
            return new Inspection(report with { Duration = time.GetElapsedTime(started) }, workspace, facts, scan);
        }
        catch (AgentUnavailableException)
        {
            await DisposeAsync(workspace);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            await DisposeAsync(workspace);
            var reason = ex is CloneException clone ? clone.Reason : ex.Message;
            return new Inspection(RepoReport.For(target, RepoVerdict.Error, reason) with { Duration = time.GetElapsedTime(started) }, null, null, SignalScan.Empty);
        }
        catch
        {
            await DisposeAsync(workspace);
            throw;
        }
    }

    public static void TryDeleteEmpty(string folder)
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

    private static async Task<SignalScan> SignalsAsync(RepoFacts facts, RepoWorkspace workspace, CancellationToken cancellationToken) =>
        await IgnoredPaths.DropIgnoredAsync(ReadmeSignals.Find(facts), workspace.Path, workspace.Git, cancellationToken);

    private async Task<(RepoReport Report, RepoFacts? Facts, SignalScan Scan)> AssessAsync(
        RepoTarget target, RepoWorkspace workspace, string outputDirectory, string? agentNote, CancellationToken cancellationToken)
    {
        var facts = await RepoFacts.CollectAsync(workspace.Path, workspace.Git, target.ReadmePath, cancellationToken);
        if (facts.Readme is null)
        {
            var note = target.ReadmePath is null ? "no README at the repository root" : $"{target.ReadmePath} is not in the repository";
            return (RepoReport.For(target, RepoVerdict.Missing, note) with { Deterministic = true }, facts, SignalScan.Empty);
        }

        var report = RepoReport.For(target, RepoVerdict.Unassessed) with
        {
            ReadmePath = facts.Readme.Path,
            ReadmeLastChanged = facts.Age?.LastChange.Date,
            CommitsSinceReadme = facts.Age?.CommitsSince,
        };

        if (facts.Readme.IsSymlink)
        {
            return (report with { Verdict = RepoVerdict.Error, Note = "the README is a symbolic link, so it wasn't checked" }, facts, SignalScan.Empty);
        }

        var scan = (await SignalsAsync(facts, workspace, cancellationToken)).Capped(Options.Readme.MaxCandidates);
        report = report with { Signals = scan.Signals, SignalsNotListed = scan.Dropped };

        var recent = facts.Age is { } age && age.CommitsSince <= Options.Readme.RecentCommits;
        if (Options.Agent.SkipWhenClean && scan.Signals.Count == 0 && recent)
        {
            return (report with
            {
                Verdict = RepoVerdict.Current,
                Deterministic = true,
                Note = $"no signals, and the README changed within the last {Options.Readme.RecentCommits} commits",
            }, facts, scan);
        }

        if (agentNote is not null)
        {
            return (scan.Signals.Any(s => s.Definitive)
                ? report with { Verdict = RepoVerdict.Stale, Deterministic = true, Note = $"broken links found; {agentNote}" }
                : report with { Note = agentNote }, facts, scan);
        }

        var logPath = Path.Combine(outputDirectory, ReportWriter.FolderName(target.Name), "agent.log");
        var outcome = await assessor.AssessAsync(target.Name, workspace.Path, facts, scan, logPath, cancellationToken);
        return (Combine(report, scan, outcome), facts, scan);
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

    private static ValueTask DisposeAsync(RepoWorkspace? workspace) => workspace?.DisposeAsync() ?? ValueTask.CompletedTask;
}
