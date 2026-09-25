using UpgradeAgent.Agent;

namespace UpgradeAgent.Tests.Agent;

public class GroupSummaryParserTests
{
    [Fact]
    public void ParsesJsonInsideACodeFenceAndFillsMissingLists()
    {
        const string Text = """
            Here you go:
            ```json
            {"packages":[{"id":"Fixture.Lib","from":"1.1.0","to":"2.0.0","status":"fixed",
              "breakingChanges":["Format renamed"],"fixes":[{"file":"src/A.cs","reason":"use FormatValue"}]}]}
            ```
            """;

        var summary = GroupSummaryParser.TryParse(Text);

        var package = Assert.Single(summary!.Packages);
        Assert.Equal(("Fixture.Lib", PackageStatus.Fixed), (package.Id, package.Status));
        Assert.Equal("src/A.cs", Assert.Single(package.Fixes).File);
        Assert.Empty(package.Unresolved);
        Assert.Empty(package.UpcomingDeprecations);
    }

    [Fact]
    public void ReadsKebabCaseStatuses()
    {
        var summary = GroupSummaryParser.TryParse("""{"packages":[{"id":"A","from":"1.0.0","to":"1.1.0","status":"no-changes-needed"}]}""");

        Assert.Equal(PackageStatus.NoChangesNeeded, Assert.Single(summary!.Packages).Status);
    }

    [Fact]
    public void AcceptsNullListsAndAnyStatusCase()
    {
        var summary = GroupSummaryParser.TryParse("""{"packages":[{"id":"A","from":"1.0.0","to":"2.0.0","status":"Fixed","unresolved":null,"fixes":null}]}""");

        var package = Assert.Single(summary!.Packages);
        Assert.Equal(PackageStatus.Fixed, package.Status);
        Assert.Empty(package.Unresolved);
        Assert.Empty(package.Fixes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("I fixed everything!")]
    [InlineData("{ not json }")]
    [InlineData("{\"other\": 1}")]
    [InlineData("""{"packages":[{"id":"A","from":"1.0.0","status":"fixed"}]}""")]
    [InlineData("""{"packages":[{"id":"A","from":"1.0.0","to":"2.0.0","status":"sort-of"}]}""")]
    public void ReturnsNullRatherThanThrowingForAnythingButAValidSummary(string? text)
    {
        Assert.Null(GroupSummaryParser.TryParse(text));
    }

    [Fact]
    public void TheSchemaIsGeneratedFromTheTypes()
    {
        Assert.Contains("\"packages\"", GroupSummaryParser.Schema, StringComparison.Ordinal);
        Assert.Contains("no-changes-needed", GroupSummaryParser.Schema, StringComparison.Ordinal);
        Assert.Contains("Repository-relative path", GroupSummaryParser.Schema, StringComparison.Ordinal);
    }
}
