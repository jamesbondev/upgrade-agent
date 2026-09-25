using UpgradeAgent.Infrastructure;
using UpgradeAgent.Build;

namespace UpgradeAgent.Detection;

internal enum OutdatedScope
{
    Latest,
    HighestMinor,
    HighestPatch,
}

/// <summary>The three views of "outdated" that two-step planning needs.</summary>
internal sealed record OutdatedReports(
    IReadOnlyList<ReportedPackage> Latest,
    IReadOnlyList<ReportedPackage> HighestMinor,
    IReadOnlyList<ReportedPackage> HighestPatch);

internal sealed class PackageListRunner(IProcessRunner processRunner)
{
    // NuGet audit queries a live vulnerability feed; it adds noise and nondeterminism to detection.
    private static readonly Dictionary<string, string?> DetectionEnvironment = new(DotnetCli.BaseEnvironment)
    {
        ["NuGetAudit"] = "false",
    };

    public async Task<OutdatedReports> ListAllAsync(string solutionPath, bool includePrerelease, CancellationToken cancellationToken)
    {
        // The first call restores; the others reuse the assets files.
        var latest = await ListAsync(solutionPath, OutdatedScope.Latest, includePrerelease, restore: true, cancellationToken);
        var minor = await ListAsync(solutionPath, OutdatedScope.HighestMinor, includePrerelease, restore: false, cancellationToken);
        var patch = await ListAsync(solutionPath, OutdatedScope.HighestPatch, includePrerelease, restore: false, cancellationToken);
        return new OutdatedReports(latest, minor, patch);
    }

    public async Task<IReadOnlyList<ReportedPackage>> ListAsync(
        string solutionPath,
        OutdatedScope scope,
        bool includePrerelease,
        bool restore,
        CancellationToken cancellationToken)
    {
        List<string> arguments = ["package", "list", "--project", solutionPath, "--outdated", "--format", "json"];
        if (scope == OutdatedScope.HighestMinor)
        {
            arguments.Add("--highest-minor");
        }
        else if (scope == OutdatedScope.HighestPatch)
        {
            arguments.Add("--highest-patch");
        }

        if (includePrerelease)
        {
            arguments.Add("--include-prerelease");
        }

        if (!restore)
        {
            arguments.Add("--no-restore");
        }

        var result = await processRunner.RunAsync(
            "dotnet", arguments, Path.GetDirectoryName(solutionPath)!, DetectionEnvironment, cancellationToken);

        if (!result.Succeeded)
        {
            throw new PackageListException(
                $"dotnet package list ({scope}) exited with code {result.ExitCode}.", result.CombinedOutput);
        }

        return PackageListParser.Parse(result.StandardOutput);
    }
}
