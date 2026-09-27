using RepoKit;
using TestHardener.Config;

namespace TestHardener.Stryker;

internal sealed record StrykerRun(MutationReport? Report, string OutputDirectory, TimeSpan Duration, string? Failure);

internal interface IStrykerRunner
{
    Task<StrykerRun> RunAsync(StrykerRequest request, string outputDirectory, IReadOnlyDictionary<string, string?> environment, CancellationToken cancellationToken);
}

internal sealed class StrykerRunner(IProcessRunner processRunner, ResolvedConfig config, StrykerTool tool, TimeProvider time) : IStrykerRunner
{
    public const string ReportRelativePath = "reports/mutation-report.json";

    public async Task<StrykerRun> RunAsync(
        StrykerRequest request, string outputDirectory, IReadOnlyDictionary<string, string?> environment, CancellationToken cancellationToken)
    {
        var options = config.Options.Stryker;
        var started = time.GetTimestamp();
        Directory.CreateDirectory(outputDirectory);
        var executable = await tool.EnsureAsync(config.StrykerToolPath, options.Version, environment, cancellationToken);

        var configPath = Path.Combine(outputDirectory, "stryker-config.json");
        await File.WriteAllTextAsync(configPath, StrykerConfig.Build(request, options), cancellationToken);

        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(request.RepoRoot, request.Target.Project)))!;
        if (!Directory.Exists(projectDirectory))
        {
            return Failed($"{request.Target.Project} is not in the repository");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(options.TimeoutMinutesPerTarget));
        ProcessResult result;
        try
        {
            result = await processRunner.RunAsync(
                executable, ["--config-file", configPath, "--output", outputDirectory], projectDirectory, environment, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failed($"Stryker didn't finish within {options.TimeoutMinutesPerTarget} minutes");
        }

        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "stryker.log"), result.CombinedOutput, cancellationToken);
        return await ReadAsync(result.ExitCode, outputDirectory, request.RepoRoot, time.GetElapsedTime(started), cancellationToken);

        StrykerRun Failed(string reason) => new(null, outputDirectory, time.GetElapsedTime(started), reason);
    }

    internal static async Task<StrykerRun> ReadAsync(
        int exitCode, string outputDirectory, string repoRoot, TimeSpan duration, CancellationToken cancellationToken)
    {
        if (exitCode != 0)
        {
            return new StrykerRun(null, outputDirectory, duration, $"Stryker exited with code {exitCode}; see stryker.log");
        }

        var reportPath = Path.Combine(outputDirectory, ReportRelativePath);
        if (!File.Exists(reportPath))
        {
            return new StrykerRun(null, outputDirectory, duration, "Stryker exited with code 0 but wrote no mutation report; see stryker.log");
        }

        MutationReport report;
        try
        {
            report = await MutationReportParser.ReadAsync(reportPath, repoRoot, cancellationToken);
        }
        catch (MutationReportException ex)
        {
            return new StrykerRun(null, outputDirectory, duration, ex.Message);
        }

        var failure = report.Mutants.Count == 0 ? "the mutation report has no mutants"
            : report.TestCount == 0 ? "the mutation report lists no tests, so Stryker probably found no test project"
            : null;
        return new StrykerRun(failure is null ? report : null, outputDirectory, duration, failure);
    }
}
