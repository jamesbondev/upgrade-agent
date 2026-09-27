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

internal sealed class SurveyOrchestrator(
    ResolvedConfig config,
    GitCli git,
    IProcessRunner processRunner,
    AzureDevOpsCredentialProvider credentials,
    IStrykerRunner stryker,
    ISurveyProgress progress,
    TimeProvider time)
{
    public async Task<SurveyReport> RunAsync(SurveyArguments arguments, CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        var startedUtc = time.GetUtcNow();
        var runId = startedUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var outputDirectory = Path.Combine(config.OutputDirectory, $"run-{runId}");
        var workRoot = Path.Combine(config.WorkRoot, runId);
        var from = arguments.From is null ? null : Path.GetFullPath(arguments.From);
        if (from is not null && !Directory.Exists(from))
        {
            throw new ConfigurationException($"--from folder not found: {from}");
        }

        var targets = Select(arguments.Only);
        var credential = targets.Any(t => t.AzureDevOps is not null) ? await credentials.AcquireAsync(cancellationToken) : null;
        var environment = SafeEnvironment.ForCurrentProcess(credentials.SecretEnvironmentVariables);

        var repos = new List<RepoSurvey>();
        try
        {
            for (var i = 0; i < targets.Count; i++)
            {
                progress.RepoStarted(targets[i], i + 1, targets.Count);
                var repoStarted = time.GetTimestamp();
                var survey = await SurveyRepoAsync(targets[i], outputDirectory, workRoot, from, credential, environment, cancellationToken);
                survey = survey with { Duration = time.GetElapsedTime(repoStarted) };
                repos.Add(survey);
                await SurveyReportWriter.WriteRepoAsync(survey, outputDirectory, cancellationToken);
                progress.RepoFinished(survey);
            }
        }
        finally
        {
            TryDeleteEmpty(workRoot);
        }

        var report = new SurveyReport(runId, startedUtc, time.GetElapsedTime(started), outputDirectory, from, repos);
        var reportPath = await SurveyReportWriter.WriteAsync(report, config.Options.Hardening.MaxGroupsPerRun, cancellationToken);
        progress.RunFinished(report, reportPath);
        return report;
    }

    private async Task<RepoSurvey> SurveyRepoAsync(
        RepoTarget target,
        string outputDirectory,
        string workRoot,
        string? from,
        AzureDevOpsCredential? credential,
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken cancellationToken)
    {
        RepoSurvey? earlier = null;
        if (from is not null)
        {
            earlier = await ReadEarlierAsync(from, target.Name, cancellationToken);
            if (earlier?.Sha is null)
            {
                return RepoSurvey.Failed(target, $"no survey.json with a commit for this repo in {from}");
            }
        }

        RepoWorkspace? workspace = null;
        try
        {
            var source = target.AzureDevOps is { } azureDevOps
                ? RepoSource.Remote(target.Name, azureDevOps.CloneUrl, credential?.AuthorizationHeader)
                : RepoSource.Local(target.Name, target.LocalPath!);
            workspace = await RepoWorkspace.CloneAsync(
                source, workRoot, git, TimeSpan.FromMinutes(config.Options.Output.CloneTimeoutMinutes), cancellationToken);
            workspace.Keep = config.Options.Output.KeepClones;

            if (earlier?.Sha is { } sha)
            {
                var checkout = await workspace.Git.TryRunAsync(workspace.Path, ["checkout", "-q", "--detach", sha], cancellationToken);
                if (!checkout.Succeeded)
                {
                    return RepoSurvey.Failed(target, $"the earlier run surveyed commit {sha}, which this clone can't check out");
                }
            }

            var head = await workspace.Git.HeadAsync(workspace.Path, cancellationToken);
            var survey = new RepoSurvey
            {
                Name = target.Name,
                Location = target.Location,
                Status = SurveyStatus.Surveyed,
                Sha = head,
                CloneRoot = workspace.Path,
            };

            if (earlier is null && await RestoreAsync(workspace.Path, target.Solution, environment, cancellationToken) is { } restoreFailure)
            {
                return survey with { Status = SurveyStatus.Failed, Note = restoreFailure };
            }

            var fixCommits = await FixCountsAsync(workspace, cancellationToken);
            var runner = TestRunnerDetector.Detect(workspace.Path);
            var results = new List<TargetSurvey>();
            foreach (var targetConfig in target.Targets)
            {
                progress.TargetStarted(targetConfig, earlier is not null);
                var result = await SurveyTargetAsync(
                    workspace.Path, targetConfig, runner, Path.Combine(outputDirectory, SurveyReportWriter.FolderName(target.Name)), earlier, from, fixCommits, environment, cancellationToken);
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
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return RepoSurvey.Failed(target, ex is CloneException clone ? clone.Reason : ex.GetBaseException().Message);
        }
        finally
        {
            if (workspace is not null)
            {
                await workspace.DisposeAsync();
            }
        }
    }

    private async Task<TargetSurvey> SurveyTargetAsync(
        string repoRoot,
        TargetConfig target,
        TestRunnerMode runner,
        string repoOutput,
        RepoSurvey? earlier,
        string? from,
        IReadOnlyDictionary<string, int> fixCommits,
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        var outputDirectory = Path.Combine(repoOutput, "stryker", target.Name);
        var survey = new TargetSurvey { Name = target.Name, Project = target.Project, TestProjects = target.TestProjects };
        try
        {
            var run = earlier is null
                ? await stryker.RunAsync(new StrykerRequest(repoRoot, target, runner, target.Mutate, target.TestFilter), outputDirectory, environment, cancellationToken)
                : await ReuseAsync(earlier, from!, target, outputDirectory, cancellationToken);
            if (run.Report is not { } report)
            {
                return survey with { Failure = run.Failure ?? "Stryker produced no report", Duration = time.GetElapsedTime(started) };
            }

            var analysis = SurvivorAnalysis.Analyze(report, target, file => ReadSource(repoRoot, file), fixCommits);
            return survey with
            {
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
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return survey with { Failure = ex.GetBaseException().Message, Duration = time.GetElapsedTime(started) };
        }
    }

    private async Task<StrykerRun> ReuseAsync(RepoSurvey earlier, string from, TargetConfig target, string outputDirectory, CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        var previous = earlier.Targets.FirstOrDefault(t => t.Name.Equals(target.Name, StringComparison.OrdinalIgnoreCase));
        if (previous?.ReportPath is not { } relative)
        {
            return new StrykerRun(null, outputDirectory, TimeSpan.Zero, "the earlier run has no report for this target");
        }

        var source = Path.GetFullPath(Path.Combine(from, SurveyReportWriter.FolderName(earlier.Name), relative));
        if (!File.Exists(source))
        {
            return new StrykerRun(null, outputDirectory, TimeSpan.Zero, $"the earlier report is missing: {source}");
        }

        var destination = Path.Combine(outputDirectory, StrykerRunner.ReportRelativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
        return await StrykerRunner.ReadAsync(0, outputDirectory, earlier.CloneRoot ?? "", time.GetElapsedTime(started), cancellationToken);
    }

    private async Task<string?> RestoreAsync(string repoRoot, string solution, IReadOnlyDictionary<string, string?> environment, CancellationToken cancellationToken)
    {
        if (!File.Exists(Path.Combine(repoRoot, solution)))
        {
            return $"{solution} is not in the repository";
        }

        var result = await processRunner.RunAsync("dotnet", ["restore", solution, "-tl:off", "-nologo"], repoRoot, environment, cancellationToken);
        return result.Succeeded ? null : $"dotnet restore {solution} failed: {Tail(result.CombinedOutput)}";
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

    private static string Tail(string output) => output.Length <= 1500 ? output.Trim() : output[^1500..].Trim();

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
