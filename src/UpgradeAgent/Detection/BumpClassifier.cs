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

internal static class PlainVersion
{
    public static bool TryParse(string? text, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out NuGetVersion? version) =>
        NuGetVersion.TryParse(text, out version);

    public static NuGetVersion Normalized(this NuGetVersion version) => new(version.Version, version.ReleaseLabels, version.Metadata, originalVersion: null);
}
