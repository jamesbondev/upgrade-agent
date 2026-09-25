using NuGet.Versioning;
using UpgradeAgent.Detection;

namespace UpgradeAgent.Tests.Detection;

public class BumpClassifierTests
{
    [Theory]
    [InlineData("13.0.1", "13.0.4", nameof(BumpKind.Patch))]
    [InlineData("1.0.0", "1.1.0", nameof(BumpKind.Minor))]
    [InlineData("1.9.9", "2.0.0", nameof(BumpKind.Major))]
    [InlineData("0.3.1", "0.3.2", nameof(BumpKind.Patch))]
    [InlineData("0.3.1", "0.4.0", nameof(BumpKind.Major))]
    [InlineData("1.0.0-beta.1", "1.0.0", nameof(BumpKind.Patch))]
    [InlineData("4.0.0.1", "4.0.0.2", nameof(BumpKind.Patch))]
    public void ClassifiesBySemVerWithZeroXMinorsAsMajor(string from, string to, string expected)
    {
        Assert.Equal(Enum.Parse<BumpKind>(expected), BumpClassifier.Classify(NuGetVersion.Parse(from), NuGetVersion.Parse(to)));
    }

    [Theory]
    [InlineData("1.0.0", true)]
    [InlineData("13.0.1", true)]
    [InlineData("1.*", false)]
    [InlineData("[1.0.0, 2.0.0)", false)]
    [InlineData("[1.0.0]", false)]
    [InlineData("$(FooVersion)", false)]
    [InlineData("", false)]
    public void OnlyPlainVersionsCanBeRewritten(string requested, bool expected)
    {
        Assert.Equal(expected, PlainVersion.TryParse(requested, out _));
    }
}
