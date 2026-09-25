using NuGet.Versioning;

namespace UpgradeAgent.Detection;

internal enum BumpKind
{
    Patch,
    Minor,
    Major,
}

internal static class BumpClassifier
{
    /// <summary>SemVer classification. Under 1.0 a minor change can break, so <c>0.x</c> minor bumps count as major.</summary>
    public static BumpKind Classify(NuGetVersion from, NuGetVersion to)
    {
        if (to.Major != from.Major)
        {
            return BumpKind.Major;
        }

        if (to.Minor != from.Minor)
        {
            return from.Major == 0 ? BumpKind.Major : BumpKind.Minor;
        }

        return BumpKind.Patch;
    }

    /// <summary>
    /// True when the requested version is a plain minimum version (what CPM and most repos use).
    /// Floating versions and explicit ranges are left for a human.
    /// </summary>
    public static bool IsPlainVersion(string requestedVersion) =>
        requestedVersion.IndexOfAny(['*', '[', ']', '(', ')', ',']) < 0
        && NuGetVersion.TryParse(requestedVersion, out _);
}
