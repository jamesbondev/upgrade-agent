using UpgradeAgent.Build;
using UpgradeAgent.Infrastructure;

namespace UpgradeAgent.Detection;

internal sealed record OutdatedReports(
    IReadOnlyList<ReportedPackage> Latest,
    IReadOnlyList<ReportedPackage> HighestMinor,
    IReadOnlyList<ReportedPackage> HighestPatch);

internal sealed class PackageListRunner(IProcessRunner processRunner)
{
    private static readonly Dictionary<string, string?> DetectionEnvironment = new(DotnetCli.BaseEnvironment)
    {
        ["NuGetAudit"] = "false",
    };

    private enum Scope
    {
        Latest,
        HighestMinor,
        HighestPatch,
    }

    public async Task<OutdatedReports> ListAllAsync(string solutionPath, bool includePrerelease, CancellationToken cancellationToken)
    {
        var latest = await ListAsync(solutionPath, Scope.Latest, includePrerelease, cancellationToken);
        var minor = await ListAsync(solutionPath, Scope.HighestMinor, includePrerelease, cancellationToken);
        var patch = await ListAsync(solutionPath, Scope.HighestPatch, includePrerelease, cancellationToken);
        return new OutdatedReports(latest, minor, patch);
    }

    private async Task<IReadOnlyList<ReportedPackage>> ListAsync(string solutionPath, Scope scope, bool includePrerelease, CancellationToken cancellationToken)
    {
        List<string> arguments = ["package", "list", "--project", solutionPath, "--outdated", "--format", "json"];
        arguments.AddRange(scope switch
        {
            Scope.HighestMinor => ["--highest-minor", "--no-restore"],
            Scope.HighestPatch => ["--highest-patch", "--no-restore"],
            _ => [],
        });
        if (includePrerelease)
        {
            arguments.Add("--include-prerelease");
        }

        var result = await processRunner.RunAsync("dotnet", arguments, Path.GetDirectoryName(solutionPath)!, DetectionEnvironment, cancellationToken);
        return result.Succeeded
            ? PackageListParser.Parse(result.StandardOutput)
            : throw new PackageListException($"dotnet package list ({scope}) exited with code {result.ExitCode}.", result.CombinedOutput);
    }
}
