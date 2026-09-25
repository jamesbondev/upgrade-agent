using NuGet.Frameworks;
using UpgradeAgent.Detection;

namespace UpgradeAgent.Tests.Detection;

public class NuGetPackageCompatibilityCheckerTests
{
    [Fact]
    public void CompatibleWhenAnySupportedFrameworkFits()
    {
        var result = Evaluate(["netstandard2.0", "net8.0"], ["net10.0"]);

        Assert.Equal(CompatibilityStatus.Compatible, result.Status);
    }

    [Fact]
    public void IncompatibleWhenThePackageNeedsANewerFramework()
    {
        var result = Evaluate(["net10.0"], ["net8.0"]);

        Assert.Equal(CompatibilityStatus.Incompatible, result.Status);
        Assert.Contains("net10.0", result.Detail, StringComparison.Ordinal);
        Assert.Contains("not net8.0", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsOnlyTheIncompatibleProjectFrameworks()
    {
        var result = Evaluate(["net10.0"], ["net8.0", "net10.0"]);

        Assert.Equal(CompatibilityStatus.Incompatible, result.Status);
        Assert.EndsWith("not net8.0", result.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void CompatibleWhenThePackageHasNoFrameworkSpecificAssets()
    {
        Assert.Equal(CompatibilityStatus.Compatible, Evaluate([], ["net8.0"]).Status);
        Assert.Equal(CompatibilityStatus.Compatible, NuGetPackageCompatibilityChecker.Evaluate([NuGetFramework.AnyFramework], [NuGetFramework.Parse("net8.0")]).Status);
    }

    private static CompatibilityResult Evaluate(string[] supported, string[] projectFrameworks) =>
        NuGetPackageCompatibilityChecker.Evaluate(supported.Select(NuGetFramework.Parse).ToList(), projectFrameworks.Select(NuGetFramework.Parse).ToList());
}
