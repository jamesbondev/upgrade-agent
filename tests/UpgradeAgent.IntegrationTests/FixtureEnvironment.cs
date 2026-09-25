using System.Diagnostics;
using System.Text.Json;

namespace UpgradeAgent.IntegrationTests;

/// <summary>
/// Builds the fixture once into a temp folder and runs the UpgradeAgent CLI against it. Every child process
/// gets its own NuGet packages folder, so the tests never touch the developer's global cache.
/// </summary>
public sealed class FixtureEnvironment : IAsyncLifetime
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromMinutes(5);

    public string RepoRoot { get; } = FindRepoRoot();

    public string Root { get; } = Directory.CreateTempSubdirectory("ua-it-").FullName;

    public string FixtureRepo => Path.Combine(Root, "fixture", "SampleRepo");

    public string ConfigPath => Path.Combine(Root, "config.json");

    private string PackagesFolder => Path.Combine(Root, "nuget-packages");

    public async Task InitializeAsync()
    {
        var build = await RunAsync("pwsh", ["-NoProfile", "-File", Path.Combine(RepoRoot, "scripts", "build-fixture.ps1"), "-OutputRoot", Path.Combine(Root, "fixture")], RepoRoot);
        if (build.ExitCode != 0)
        {
            throw new InvalidOperationException($"build-fixture.ps1 failed:\n{build.Output}");
        }

        await File.WriteAllTextAsync(ConfigPath, JsonSerializer.Serialize(new
        {
            Target = new { RepoPath = FixtureRepo, Solution = "LoanLedger.slnx" },
            Policy = new { Deny = new[] { new { Id = "xunit*", Reason = "pinned" }, new { Id = "Microsoft.NET.Test.Sdk", Reason = "pinned" } } },
            Output = new { Directory = Path.Combine(Root, "out"), RecordingsDirectory = Path.Combine(RepoRoot, "recordings") },
        }));
    }

    public async Task<(int ExitCode, string Output, JsonElement Report, string Worktree)> ReplayAsync(string recording)
    {
        await ResetAsync();
        var cli = Path.Combine(AppContext.BaseDirectory, "UpgradeAgent.dll");
        var (exitCode, output) = await RunAsync("dotnet", [cli, "run", "--config", ConfigPath, "--replay", recording, "--replay-max-gap", "0", "--non-interactive"], Root);

        var outDirectory = Path.Combine(Root, "out");
        var runJson = Directory.Exists(outDirectory) ? Directory.GetFiles(outDirectory, "run.json", SearchOption.AllDirectories).SingleOrDefault() : null;
        if (runJson is null)
        {
            throw new InvalidOperationException($"The run produced no run.json (exit code {exitCode}):\n{output}");
        }

        var report = JsonDocument.Parse(await File.ReadAllTextAsync(runJson)).RootElement.Clone();
        return (exitCode, output, report, report.GetProperty("worktreePath").GetString()!);
    }

    public async Task DisposeAsync()
    {
        await RunAsync("dotnet", ["build-server", "shutdown"], Root);
        try
        {
            // git marks object files read-only; Windows refuses to delete read-only files.
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: a lingering process may still hold a file on Windows.
        }
    }

    private async Task<(int ExitCode, string Output)> RunAsync(string file, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo(file) { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["COLUMNS"] = "200";
        startInfo.Environment["NUGET_PACKAGES"] = PackagesFolder;
        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(ProcessTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"'{file} {string.Join(' ', arguments)}' did not finish within {ProcessTimeout.TotalMinutes} minutes.");
        }

        return (process.ExitCode, await stdout + await stderr);
    }

    private async Task ResetAsync()
    {
        var reset = await RunAsync(
            "pwsh", ["-NoProfile", "-File", Path.Combine(RepoRoot, "scripts", "reset-demo.ps1"), "-RepoPath", FixtureRepo, "-OutputDirectory", Path.Combine(Root, "out")], Root);
        if (reset.ExitCode != 0)
        {
            throw new InvalidOperationException($"reset-demo.ps1 failed:\n{reset.Output}");
        }
    }

    private static string FindRepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "UpgradeAgent.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Could not find the repository root (UpgradeAgent.slnx).");
    }
}
