using System.Diagnostics;
using System.Text.Json;
using UpgradeAgent.Guardrails;
using UpgradeAgent.Infrastructure;

namespace UpgradeAgent.Build;

public enum TestRunnerMode
{
    /// <summary>Classic VSTest: <c>--logger trx</c>.</summary>
    VSTest,

    /// <summary>Microsoft.Testing.Platform selected in global.json: <c>--solution</c> and <c>--report-trx</c>.</summary>
    TestingPlatform,
}

public sealed record BuildResult(bool Succeeded, IReadOnlyList<Diagnostic> Errors, IReadOnlyList<Diagnostic> Warnings, string Output, TimeSpan Duration);

/// <param name="Inventory">Per-method results from TRX; null when no TRX was produced (count-only fallback).</param>
public sealed record TestRunResult(bool Succeeded, TestInventory? Inventory, TestCounts? Counts, string Output, TimeSpan Duration);

public sealed class DotnetCli(IProcessRunner processRunner)
{
    /// <summary>
    /// No node reuse and no build server: agent-triggered builds must never run inside MSBuild nodes
    /// started with the app's full environment, and lingering nodes lock worktree files on Windows.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string?> Environment = new Dictionary<string, string?>
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

        var stopwatch = Stopwatch.StartNew();
        var result = await processRunner.RunAsync("dotnet", arguments, Path.GetDirectoryName(solutionPath)!, Environment, cancellationToken);

        var trxFiles = Directory.Exists(resultsDirectory)
            ? Directory.GetFiles(resultsDirectory, "*.trx", SearchOption.AllDirectories)
            : [];
        var inventory = trxFiles.Length > 0 ? TrxParser.Parse(trxFiles) : null;

        return new TestRunResult(result.Succeeded, inventory, BuildOutputParser.ParseTestCounts(result.StandardOutput), result.CombinedOutput, stopwatch.Elapsed);
    }

    public async Task<string> SdkVersionAsync(string workingDirectory, CancellationToken cancellationToken) =>
        (await processRunner.RunAsync("dotnet", ["--version"], workingDirectory, Environment, cancellationToken)).StandardOutput.Trim();

    /// <summary>global.json <c>"test": { "runner": "Microsoft.Testing.Platform" }</c> switches dotnet test to MTP mode.</summary>
    public static TestRunnerMode DetectRunnerMode(string repoRoot)
    {
        var globalJson = Path.Combine(repoRoot, "global.json");
        if (!File.Exists(globalJson))
        {
            return TestRunnerMode.VSTest;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(globalJson), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return document.RootElement.TryGetProperty("test", out var test)
                && test.TryGetProperty("runner", out var runner)
                && string.Equals(runner.GetString(), "Microsoft.Testing.Platform", StringComparison.OrdinalIgnoreCase)
                    ? TestRunnerMode.TestingPlatform
                    : TestRunnerMode.VSTest;
        }
        catch (JsonException)
        {
            return TestRunnerMode.VSTest;
        }
    }

    private async Task<BuildResult> RunBuildLikeAsync(string solutionPath, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await processRunner.RunAsync("dotnet", arguments, Path.GetDirectoryName(solutionPath)!, Environment, cancellationToken);
        var (errors, warnings) = BuildOutputParser.ParseDiagnostics(result.CombinedOutput);
        return new BuildResult(result.Succeeded, errors, warnings, result.CombinedOutput, stopwatch.Elapsed);
    }
}
