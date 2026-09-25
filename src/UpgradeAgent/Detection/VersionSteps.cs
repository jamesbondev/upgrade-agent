using NuGet.Frameworks;
using NuGet.Versioning;
using UpgradeAgent.Config;
using UpgradeAgent.Infrastructure;

namespace UpgradeAgent.Detection;

/// <summary>One version step for one package in one project, before any policy is applied.</summary>
/// <param name="ManualReason">Set when the app can't make the change itself (a range or floating version).</param>
internal sealed record VersionStep(string Id, NuGetVersion From, NuGetVersion To, BumpKind Kind, ProjectTarget Project, string? ManualReason);

/// <summary>
/// Turns the outdated reports into version steps. A major with a newer release in the current line becomes
/// two steps (to the latest minor, then to the major), so the non-breaking part lands even if the major can't.
/// Pure: no IO, no policy.
/// </summary>
internal static class VersionSteps
{
    public static IEnumerable<VersionStep> Build(OutdatedReports reports, IReadOnlyDictionary<string, string> targetOverrides, string repoRoot) =>
        reports.Latest.SelectMany(package => ForPackage(package, reports, targetOverrides, repoRoot));

    private static IEnumerable<VersionStep> ForPackage(
        ReportedPackage package, OutdatedReports reports, IReadOnlyDictionary<string, string> targetOverrides, string repoRoot)
    {
        if (!NuGetVersion.TryParse(package.ResolvedVersion, out var resolved) || !NuGetVersion.TryParse(package.LatestVersion, out var latestReported))
        {
            yield break;
        }

        var current = resolved.Normalized();
        var latest = latestReported.Normalized();

        var project = new ProjectTarget(RepoPath.Relative(repoRoot, package.ProjectPath), NuGetFramework.Parse(package.Framework));
        var final = latest;
        if (targetOverrides.TryGetValue(package.Id, out var overrideVersion))
        {
            final = NuGetVersion.TryParse(overrideVersion, out var parsed)
                ? parsed.Normalized()
                : throw new ConfigurationException($"Policy:TargetOverrides:{package.Id} is not a valid version: '{overrideVersion}'.");
        }

        if (final <= current)
        {
            yield break;
        }

        var kind = BumpClassifier.Classify(current, final);
        if (!PlainVersion.TryParse(package.RequestedVersion, out _))
        {
            var why = package.RequestedVersion is null ? "no requested version was reported" : $"version is a range or floating ('{package.RequestedVersion}')";
            yield return new VersionStep(package.Id, current, final, kind, project, why);
            yield break;
        }

        if (kind == BumpKind.Major && FindIntermediate(package, current, final, reports) is { } intermediate)
        {
            yield return new VersionStep(package.Id, current, intermediate, BumpClassifier.Classify(current, intermediate), project, null);
            yield return new VersionStep(package.Id, intermediate, final, BumpKind.Major, project, null);
            yield break;
        }

        yield return new VersionStep(package.Id, current, final, kind, project, null);
    }

    /// <summary>
    /// The newest non-breaking version before the major: latest minor for 1.0+, latest patch for 0.x.
    /// A package with nothing newer in its current line is absent from that report.
    /// </summary>
    private static NuGetVersion? FindIntermediate(ReportedPackage package, NuGetVersion current, NuGetVersion final, OutdatedReports reports)
    {
        var source = current.Major == 0 ? reports.HighestPatch : reports.HighestMinor;
        var match = source.FirstOrDefault(p =>
            string.Equals(p.Id, package.Id, StringComparison.OrdinalIgnoreCase)
            && string.Equals(p.ProjectPath, package.ProjectPath, StringComparison.Ordinal)
            && string.Equals(p.Framework, package.Framework, StringComparison.OrdinalIgnoreCase));

        return match is not null
            && NuGetVersion.TryParse(match.LatestVersion, out var candidate)
            && candidate > current
            && candidate < final
            && BumpClassifier.Classify(current, candidate) != BumpKind.Major
                ? candidate.Normalized()
                : null;
    }
}
