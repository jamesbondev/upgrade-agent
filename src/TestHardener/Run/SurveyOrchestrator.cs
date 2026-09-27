using System.Globalization;
using System.Text.Json;
using RepoKit;
using RepoKit.AzureDevOps;
using TestHardener.Analysis;
using TestHardener.Config;
using TestHardener.Infrastructure;
using TestHardener.Reporting;
using TestHardener.Stryker;

namespace TestHardener.Run;

internal sealed record RunContext(
    string RunId,
    DateTimeOffset StartedUtc,
    long StartedTimestamp,
    string OutputDirectory,
    string WorkRoot,
    string? From,
    IReadOnlyList<RepoTarget> Targets,
    AzureDevOpsCredential? Credential,
    IReadOnlyDictionary<string, string?> Environment)
{
    public string RepoOutput(string repoName) => Path.Combine(OutputDirectory, SurveyReportWriter.FolderName(repoName));
}

internal sealed class SurveyedRepo(RepoSurvey survey, RepoWorkspace? workspace) : IAsyncDisposable
{
    public RepoSurvey Survey { get; } = survey;

    public RepoWorkspace? Workspace { get; } = workspace;

    public ValueTask DisposeAsync() => Workspace?.DisposeAsync() ?? ValueTask.CompletedTask;
}

internal sealed class RepoSurveyor(
    ResolvedConfig config,
    GitCli git,
    IProcessRunner processRunner,
    AzureDevOpsCredentialProvider credentials,
    IStrykerRunner stryker,
    ISurveyProgress progress,
    TimeProvider time)
{
    public async Task<RunContext> PrepareAsync(IReadOnlyCollection<string> only, string? from, CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        var startedUtc = time.GetUtcNow();
        var runId = startedUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var fullFrom = from is null ? null : Path.GetFullPath(from);
        if (fullFrom is not null && !Directory.Exists(fullFrom))
        {
            throw new ConfigurationException($"--from folder not found: {fullFrom}");
        }

        var targets = Select(only);
        var credential = targets.Any(t => t.AzureDevOps is not null) ? await credentials.AcquireAsync(cancellationToken) : null;
        return new RunContext(
            runId,
            startedUtc,
            started,
            Path.Combine(config.OutputDirectory, $"run-{runId}"),
            Path.Combine(config.WorkRoot, runId),
            fullFrom,
            targets,
            credential,
            SafeEnvironment.ForCurrentProcess(credentials.SecretEnvironmentVariables));
    }

    public async Task<SurveyedRepo> SurveyAsync(RepoTarget target, RunContext run, CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        var surveyed = await SurveyRepoAsync(target, run, cancellationToken);
        var survey = surveyed.Survey with { Duration = time.GetElapsedTime(started) };
        await SurveyReportWriter.WriteRepoAsync(survey, run.OutputDirectory, cancellationToken);
        return new SurveyedRepo(survey, surveyed.Workspace);
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

    private async Task<SurveyedRepo> SurveyRepoAsync(RepoTarget target, RunContext run, CancellationToken cancellationToken)
    {
        RepoSurvey? earlier = null;
        if (run.From is { } from)
        {
            earlier = await ReadEarlierAsync(from, target.Name, cancellationToken);
            if (earlier?.Sha is null || earlier.CloneRoot is null)
            {
                return new SurveyedRepo(RepoSurvey.Failed(target, $"no survey.json with a commit and clone path for this repo in {from}"), null);
            }
        }

        RepoWorkspace? workspace = null;
        try
        {
            var source = target.AzureDevOps is { } azureDevOps
                ? RepoSource.Remote(target.Name, azureDevOps.CloneUrl, run.Credential?.AuthorizationHeader)
                : RepoSource.Local(target.Name, target.LocalPath!);
            workspace = await RepoWorkspace.CloneAsync(
                source, run.WorkRoot, git, TimeSpan.FromMinutes(config.Options.Output.CloneTimeoutMinutes), cancellationToken);
            workspace.Keep = config.Options.Output.KeepClones;
            var survey = await SurveyCloneAsync(target, run, workspace, earlier, cancellationToken);
            return new SurveyedRepo(survey, workspace);
        }
        catch (Exception ex) when (ex is not StrykerToolException && (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested))
        {
            if (workspace is not null)
            {
                await workspace.DisposeAsync();
            }

            return new SurveyedRepo(RepoSurvey.Failed(target, ex is CloneException clone ? clone.Reason : ex.GetBaseException().Message), null);
        }
        catch
        {
            if (workspace is not null)
            {
                await workspace.DisposeAsync();
            }

            throw;
        }
    }

    private async Task<RepoSurvey> SurveyCloneAsync(
        RepoTarget target, RunContext run, RepoWorkspace workspace, RepoSurvey? earlier, CancellationToken cancellationToken)
    {
        if (earlier?.Sha is { } sha)
        {
            var checkout = await workspace.Git.TryRunAsync(workspace.Path, ["checkout", "-q", "--detach", sha], cancellationToken);
            if (!checkout.Succeeded)
            {
                return RepoSurvey.Failed(target, $"the earlier run surveyed commit {sha}, which this clone can't check out");
            }
        }

        var survey = new RepoSurvey
        {
            Name = target.Name,
            Location = target.Location,
            Status = SurveyStatus.Surveyed,
            Sha = await workspace.Git.HeadAsync(workspace.Path, cancellationToken),
            CloneRoot = workspace.Path,
        };

        if (await RestoreAsync(workspace.Path, target.Solution, run.Environment, cancellationToken) is { } restoreFailure)
        {
            return survey with { Status = SurveyStatus.Failed, Note = restoreFailure };
        }

        var fixCommits = await FixCountsAsync(workspace, cancellationToken);
        var runner = TestRunnerDetector.Detect(workspace.Path);
        var results = new List<TargetSurvey>();
        foreach (var targetConfig in target.Targets)
        {
            progress.TargetStarted(targetConfig, earlier is not null);
            var result = await SurveyTargetAsync(workspace.Path, targetConfig, runner, run.RepoOutput(target.Name), earlier, run, fixCommits, cancellationToken);
            results.Add(result);
            progress.TargetFinished(result);
        }

        var failed = results.Count(r => r.Failure is not null);
        return survey with
        {
            Targets = results,
            Status = failed == 0 ? SurveyStatus.Surveyed : failed == results.Count ? SurveyStatus.Failed : SurveyStatus.Partial,
            Note = failed == 0 ? null : $"{failed} of {results.Count} targets failed",
        };
    }

    private async Task<TargetSurvey> SurveyTargetAsync(
        string repoRoot,
        TargetConfig target,
        TestRunnerMode runner,
        string repoOutput,
        RepoSurvey? earlier,
        RunContext run,
        IReadOnlyDictionary<string, int> fixCommits,
        CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        var outputDirectory = Path.Combine(repoOutput, "stryker", target.Name);
        var survey = new TargetSurvey { Name = target.Name, Project = target.Project, TestProjects = target.TestProjects };
        try
        {
            var (result, reportRoot) = earlier is null
                ? (await stryker.RunAsync(new StrykerRequest(repoRoot, target, runner, target.Mutate, target.TestFilter), outputDirectory, run.Environment, cancellationToken), repoRoot)
                : await ReuseAsync(earlier, run.From!, target, outputDirectory, cancellationToken);
            if (result.Report is not { } report)
            {
                return survey with { Failure = result.Failure ?? "Stryker produced no report", Duration = time.GetElapsedTime(started) };
            }

            var analysis = SurvivorAnalysis.Analyze(report, target, file => ReadSource(repoRoot, file), fixCommits);
            if (SourcesMissing(report, analysis) is { } mismatch)
            {
                return survey with { Failure = mismatch, Duration = time.GetElapsedTime(started) };
            }

            return survey with
            {
                ReportRoot = reportRoot,
                ReportPath = Path.GetRelativePath(repoOutput, Path.Combine(outputDirectory, StrykerRunner.ReportRelativePath)).Replace('\\', '/'),
                StatusCounts = report.StatusCounts,
                Score = report.Score,
                Tests = report.TestCount,
                Survivors = report.Mutants.Count(m => m.Status == MutantStatus.Survived),
                Skipped = analysis.Skipped,
                Groups = analysis.Groups,
                NotMutated = analysis.NotMutated,
                Untested = analysis.Untested,
                Duration = time.GetElapsedTime(started),
            };
        }
        catch (Exception ex) when (ex is not StrykerToolException && (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested))
        {
            return survey with { Failure = ex.GetBaseException().Message, Duration = time.GetElapsedTime(started) };
        }
    }

    internal static string? SourcesMissing(MutationReport report, TargetAnalysis analysis)
    {
        var missing = analysis.Skipped.GetValueOrDefault(SkipReason.SourceMissing);
        return missing > 0 && analysis.Groups.Count == 0
            ? $"none of the {missing} survivors' source files were found in this clone, so the report's paths don't match it (the report names files like {report.Mutants[0].File})"
            : null;
    }

    private async Task<(StrykerRun Run, string? ReportRoot)> ReuseAsync(
        RepoSurvey earlier, string from, TargetConfig target, string outputDirectory, CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        var previous = earlier.Targets.FirstOrDefault(t => t.Name.Equals(target.Name, StringComparison.OrdinalIgnoreCase));
        if (previous?.ReportPath is not { } relative)
        {
            return (new StrykerRun(null, outputDirectory, TimeSpan.Zero, "the earlier run has no report for this target"), null);
        }

        var source = Path.GetFullPath(Path.Combine(from, SurveyReportWriter.FolderName(earlier.Name), relative));
        if (!File.Exists(source))
        {
            return (new StrykerRun(null, outputDirectory, TimeSpan.Zero, $"the earlier report is missing: {source}"), null);
        }

        var destination = Path.Combine(outputDirectory, StrykerRunner.ReportRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
        var reportRoot = previous.ReportRoot ?? earlier.CloneRoot!;
        return (await StrykerRunner.ReadAsync(0, outputDirectory, reportRoot, time.GetElapsedTime(started), cancellationToken), reportRoot);
    }

    private async Task<string?> RestoreAsync(string repoRoot, string solution, IReadOnlyDictionary<string, string?> environment, CancellationToken cancellationToken)
    {
        if (!File.Exists(Path.Combine(repoRoot, solution)))
        {
            return $"{solution} is not in the repository";
        }

        var result = await processRunner.RunAsync("dotnet", ["restore", solution, "-tl:off", "-nologo"], repoRoot, environment, cancellationToken);
        return result.Succeeded ? null : $"dotnet restore {solution} failed: {TextFormat.Tail(result.CombinedOutput, 1500)}";
    }

    private async Task<IReadOnlyDictionary<string, int>> FixCountsAsync(RepoWorkspace workspace, CancellationToken cancellationToken)
    {
        try
        {
            return await FixHistory.CountAsync(
                workspace.Git, workspace.Path, time.GetUtcNow().AddDays(-config.Options.Hardening.FixHistoryDays), cancellationToken);
        }
        catch (GitException)
        {
            return new Dictionary<string, int>();
        }
    }

    private static async Task<RepoSurvey?> ReadEarlierAsync(string from, string repoName, CancellationToken cancellationToken)
    {
        var path = Path.Combine(from, SurveyReportWriter.FolderName(repoName), SurveyReportWriter.RepoFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<RepoSurvey>(await File.ReadAllTextAsync(path, cancellationToken), SurveyReportWriter.Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ReadSource(string repoRoot, string file)
    {
        var path = Path.GetFullPath(Path.Combine(repoRoot, file));
        var root = Path.GetFullPath(repoRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.Ordinal) && File.Exists(path) ? File.ReadAllText(path) : null;
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
}

internal sealed class SurveyOrchestrator(ResolvedConfig config, RepoSurveyor surveyor, ISurveyProgress progress, TimeProvider time)
{
    public async Task<SurveyReport> RunAsync(SurveyArguments arguments, CancellationToken cancellationToken)
    {
        var run = await surveyor.PrepareAsync(arguments.Only, arguments.From, cancellationToken);
        var repos = new List<RepoSurvey>();
        try
        {
            for (var i = 0; i < run.Targets.Count; i++)
            {
                progress.RepoStarted(run.Targets[i], i + 1, run.Targets.Count);
                await using var surveyed = await surveyor.SurveyAsync(run.Targets[i], run, cancellationToken);
                repos.Add(surveyed.Survey);
                progress.RepoFinished(surveyed.Survey);
            }
        }
        finally
        {
            RepoSurveyor.TryDeleteEmpty(run.WorkRoot);
        }

        var report = new SurveyReport(run.RunId, run.StartedUtc, time.GetElapsedTime(run.StartedTimestamp), run.OutputDirectory, run.From, repos);
        var reportPath = await SurveyReportWriter.WriteAsync(report, config.Options.Hardening.MaxGroupsPerRun, cancellationToken);
        progress.RunFinished(report, reportPath);
        return report;
    }
}
