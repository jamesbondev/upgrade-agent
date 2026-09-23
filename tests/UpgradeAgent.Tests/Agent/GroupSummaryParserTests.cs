using UpgradeAgent.Agent;

namespace UpgradeAgent.Tests.Agent;

public class GroupSummaryParserTests
{
    [Fact]
    public void ParsesJsonInsideACodeFenceAndFillsMissingLists()
    {
        const string text = """
            Here you go:
            ```json
            {"packages":[{"id":"Fixture.Lib","from":"1.1.0","to":"2.0.0","status":"fixed",
              "breakingChanges":["Format renamed"],"fixes":[{"file":"src/A.cs","reason":"use FormatValue"}]}]}
            ```
            """;

        var summary = GroupSummaryParser.TryParse(text);

        var package = Assert.Single(summary!.Packages);
        Assert.Equal(("Fixture.Lib", "fixed"), (package.Id, package.Status));
        Assert.Equal("src/A.cs", Assert.Single(package.Fixes).File);
        Assert.Empty(package.Unresolved);
        Assert.Empty(package.UpcomingDeprecations);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("I fixed everything!")]
    [InlineData("{ not json }")]
    [InlineData("{\"other\": 1}")]
    public void ReturnsNullRatherThanThrowing(string? text)
    {
        Assert.Null(GroupSummaryParser.TryParse(text));
    }
}
