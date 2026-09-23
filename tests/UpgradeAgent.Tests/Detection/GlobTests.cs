using UpgradeAgent.Infrastructure;

namespace UpgradeAgent.Tests.Detection;

public class GlobTests
{
    [Theory]
    [InlineData("xunit*", "xunit.runner.visualstudio", true)]
    [InlineData("xunit*", "XUnit", true)]
    [InlineData("Microsoft.Extensions.*", "Microsoft.Extensions.Http", true)]
    [InlineData("Microsoft.Extensions.*", "Microsoft.Extensions", false)]
    [InlineData("Foo.?ar", "Foo.Bar", true)]
    [InlineData("Foo.Bar", "Foo.Bar.Baz", false)]
    [InlineData("Foo(1)", "Foo(1)", true)]
    public void IsMatch_SupportsStarAndQuestionMark(string pattern, string value, bool expected)
    {
        Assert.Equal(expected, Glob.IsMatch(pattern, value));
    }
}
