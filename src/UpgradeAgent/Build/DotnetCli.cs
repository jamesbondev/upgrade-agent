using UpgradeAgent.Guardrails;
using UpgradeAgent.Infrastructure;

namespace UpgradeAgent.Build;


internal sealed record BuildResult(bool Succeeded, IReadOnlyList<Diagnostic> Errors, IReadOnlyList<Diagnostic> Warnings, string Output, TimeSpan Duration);

/// <param name="Inventory">Per-method results from TRX; null when no TRX was produced (count-only fallback).</param>
internal sealed record TestRunResult(bool Succeeded, TestInventory? Inventory, TestCounts? Counts, string Output, TimeSpan Duration)
{
    /// <summary>From TRX when there is one, else from the console summary; null when neither was readable.</summary>
    public int? Passed => Inventory?.Passed ?? Counts?.Passed;

    public int? Failed => Inventory?.Failed ?? Counts?.Failed;
}

internal sealed class DotnetCli(IProcessRunner processRunner, TimeProvider time)
{
    /// <summary>
    /// No node reuse and no build server: agent-triggered builds must never run inside MSBuild nodes
    /// started with the app's full environment, and lingering nodes lock worktree files on Windows.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string?> BaseEnvironment = new Dictionary<string, string?>
    {
        ["DOTNET_CLI_UI_LANGUAGE"] = "en",
        ["DOTNET_NOLOGO"] = "1",
        ["MSBUILDDISABLENODEREUSE"] = "1",
        ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0",
    };

    public async Task<BuildResult> RestoreAsync(string solutionPath, bool forceEvaluate, CancellationToken cancellationToken)
    {
        List<string> arguments = ["restore", solutionPath, "-tl:off", "-nologo"];
        if (forceEvaluate)
        {
            arguments.Add("--force-evaluate");
        }

        return await RunBuildLikeAsync(solutionPath, arguments, cancellationToken);
    }

    public Task<BuildResult> BuildAsync(string solutionPath, CancellationToken cancellationToken) =>
        RunBuildLikeAsync(solutionPath, ["build", solutionPath, "--no-restore", "-tl:off", "-nologo", "-clp:NoSummary"], cancellationToken);

    public async Task<TestRunResult> TestAsync(
        string solutionPath, string resultsDirectory, TestRunnerMode mode, IReadOnlyList<string> extraArguments, CancellationToken cancellationToken)
    {
        if (Directory.Exists(resultsDirectory))
        {
            Directory.Delete(resultsDirectory, recursive: true);
        }

        List<string> arguments = mode == TestRunnerMode.TestingPlatform
            ? ["test", "--solution", solutionPath, "--no-build", "--report-trx", "--results-directory", resultsDirectory]
            : ["test", solutionPath, "--no-build", "-tl:off", "-nologo", "--logger", "trx;LogFilePrefix=ua", "--results-directory", resultsDirectory];
        arguments.AddRange(extraArguments);

        var started = time.GetTimestamp();
        var result = await processRunner.RunAsync("dotnet", arguments, Path.GetDirectoryName(solutionPath)!, BaseEnvironment, cancellationToken);

        var trxFiles = Directory.Exists(resultsDirectory)
            ? Directory.GetFiles(resultsDirectory, "*.trx", SearchOption.AllDirectories)
            : [];
        var inventory = trxFiles.Length > 0 ? TrxParser.Parse(trxFiles) : null;

        return new TestRunResult(result.Succeeded, inventory, BuildOutputParser.ParseTestCounts(result.StandardOutput), result.CombinedOutput, time.GetElapsedTime(started));
    }

    /// <summary><c>dotnet --version</c> in <paramref name="workingDirectory"/>, whose global.json decides which SDK is selected.</summary>
    public Task<ProcessResult> VersionAsync(string workingDirectory, CancellationToken cancellationToken) =>
        processRunner.RunAsync("dotnet", ["--version"], workingDirectory, BaseEnvironment, cancellationToken);

    public async Task<string> SdkVersionAsync(string workingDirectory, CancellationToken cancellationToken) =>
        (await VersionAsync(workingDirectory, cancellationToken)).StandardOutput.Trim();

    private async Task<BuildResult> RunBuildLikeAsync(string solutionPath, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        var result = await processRunner.RunAsync("dotnet", arguments, Path.GetDirectoryName(solutionPath)!, BaseEnvironment, cancellationToken);
        var (errors, warnings) = BuildOutputParser.ParseDiagnostics(result.CombinedOutput);
        return new BuildResult(result.Succeeded, errors, warnings, result.CombinedOutput, time.GetElapsedTime(started));
    }
}
