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
}

/// <summary>
/// The one definition of a version the app may rewrite: a plain minimum version, as CPM and most repos use.
/// Floating versions (<c>1.*</c>), ranges (<c>[1.0,2.0)</c>) and MSBuild properties don't parse and are left for a human.
/// </summary>
internal static class PlainVersion
{
    public static bool TryParse(string? text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out NuGetVersion? version) =>
        NuGetVersion.TryParse(text, out version);
}
