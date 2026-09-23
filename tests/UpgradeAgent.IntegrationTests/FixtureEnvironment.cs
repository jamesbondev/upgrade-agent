using System.Diagnostics;
using System.Text.Json;

namespace UpgradeAgent.IntegrationTests;

/// <summary>Builds the fixture once into a temp folder and runs the UpgradeAgent CLI against it.</summary>
public sealed class FixtureEnvironment : IDisposable
{
    public FixtureEnvironment()
    {
        RepoRoot = FindRepoRoot();
        Root = Directory.CreateTempSubdirectory("ua-it-").FullName;
        FixtureRepo = Path.Combine(Root, "fixture", "SampleRepo");

        var build = Run("pwsh", ["-NoProfile", "-File", Path.Combine(RepoRoot, "scripts", "build-fixture.ps1"), "-OutputRoot", Path.Combine(Root, "fixture")], RepoRoot);
        if (build.ExitCode != 0)
        {
            throw new InvalidOperationException($"build-fixture.ps1 failed:\n{build.Output}");
        }

        ConfigPath = Path.Combine(Root, "config.json");
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(new
        {
            Target = new { RepoPath = FixtureRepo, Solution = "LoanLedger.slnx" },
            Policy = new { Deny = new[] { new { Id = "xunit*", Reason = "pinned" }, new { Id = "Microsoft.NET.Test.Sdk", Reason = "pinned" } } },
            Output = new { Directory = Path.Combine(Root, "out"), RecordingsDirectory = Path.Combine(RepoRoot, "recordings") },
        }));
    }

    public string RepoRoot { get; }

    public string Root { get; }

    public string FixtureRepo { get; }

    public string ConfigPath { get; }

    public (int ExitCode, string Output, JsonElement Report, string Worktree) Replay(string recording)
    {
        Reset();
        var cli = Path.Combine(AppContext.BaseDirectory, "UpgradeAgent.dll");
        var (exitCode, output) = Run("dotnet", [cli, "run", "--config", ConfigPath, "--replay", recording, "--replay-max-gap", "0", "--non-interactive"], Root);

        var outDirectory = Path.Combine(Root, "out");
        var runJson = Directory.Exists(outDirectory) ? Directory.GetFiles(outDirectory, "run.json", SearchOption.AllDirectories).SingleOrDefault() : null;
        if (runJson is null)
        {
            throw new InvalidOperationException($"The run produced no run.json (exit code {exitCode}):\n{output}");
        }

        var report = JsonDocument.Parse(File.ReadAllText(runJson)).RootElement.Clone();
        return (exitCode, output, report, report.GetProperty("worktreePath").GetString()!);
    }

    public static (int ExitCode, string Output) Run(string file, IReadOnlyList<string> arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo(file) { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["COLUMNS"] = "200";
        using var process = Process.Start(startInfo)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, stdout.Result + stderr.Result);
    }

    public void Dispose()
    {
        Run("dotnet", ["build-server", "shutdown"], Root);
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

    private void Reset()
    {
        var reset = Run("pwsh", ["-NoProfile", "-File", Path.Combine(RepoRoot, "scripts", "reset-demo.ps1"), "-RepoPath", FixtureRepo, "-OutputDirectory", Path.Combine(Root, "out")], Root);
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
