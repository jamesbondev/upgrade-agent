using UpgradeAgent.Detection;

namespace UpgradeAgent.Tests.Detection;

public class PackageListParserTests
{
    // Captured from `dotnet package list --outdated --format json` (SDK 10.0.112) against the fixture.
    private const string FixtureOutput = """
        {
          "version": 1,
          "parameters": "--outdated",
          "sources": [ "/repo/.feed", "https://api.nuget.org/v3/index.json" ],
          "projects": [
            {
              "path": "/repo/src/LoanLedger/LoanLedger.csproj",
              "frameworks": [
                {
                  "framework": "net10.0",
                  "topLevelPackages": [
                    { "id": "Fixture.Lib", "requestedVersion": "1.0.0", "resolvedVersion": "1.0.0", "latestVersion": "2.0.0" },
                    { "id": "Newtonsoft.Json", "requestedVersion": "13.0.1", "resolvedVersion": "13.0.1", "latestVersion": "13.0.4" }
                  ]
                }
              ]
            },
            { "path": "/repo/tests/LoanLedger.Tests/LoanLedger.Tests.csproj" }
          ]
        }
        """;

    [Fact]
    public void Parse_ReadsPackagesPerProjectAndFramework()
    {
        var packages = PackageListParser.Parse(FixtureOutput);

        Assert.Equal(2, packages.Count);
        Assert.Equal(
            new ReportedPackage("/repo/src/LoanLedger/LoanLedger.csproj", "net10.0", "Fixture.Lib", "1.0.0", "1.0.0", "2.0.0"),
            packages[0]);
        Assert.Equal("Newtonsoft.Json", packages[1].Id);
    }

    [Fact]
    public void Parse_SkipsProjectsWithoutFrameworks()
    {
        var packages = PackageListParser.Parse(FixtureOutput);

        Assert.DoesNotContain(packages, p => p.ProjectPath.Contains("Tests", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_ReadsMultiTargetedProjects()
    {
        const string output = """
            { "version": 1, "projects": [ { "path": "/repo/a.csproj", "frameworks": [
              { "framework": "net8.0", "topLevelPackages": [ { "id": "Foo", "requestedVersion": "1.0.0", "resolvedVersion": "1.0.0", "latestVersion": "2.0.0" } ] },
              { "framework": "net10.0", "topLevelPackages": [ { "id": "Foo", "requestedVersion": "1.0.0", "resolvedVersion": "1.0.0", "latestVersion": "2.0.0" } ] }
            ] } ] }
            """;

        var packages = PackageListParser.Parse(output);

        Assert.Equal(["net8.0", "net10.0"], packages.Select(p => p.Framework));
    }

    [Fact]
    public void Parse_AllowsMissingLatestVersion()
    {
        const string output = """
            { "version": 1, "projects": [ { "path": "/repo/a.csproj", "frameworks": [
              { "framework": "net10.0", "topLevelPackages": [ { "id": "Foo", "requestedVersion": "1.0.0", "resolvedVersion": "1.0.0" } ] }
            ] } ] }
            """;

        Assert.Null(Assert.Single(PackageListParser.Parse(output)).LatestVersion);
    }

    [Fact]
    public void Parse_ThrowsWithRawOutputWhenOutputIsNotJson()
    {
        // Captured: an unreachable feed makes the CLI print plain text and exit 1.
        const string output = """
            error: Unable to load the service index for source https://nonexistent.invalid/v3/index.json.
            error:   Name or service not known (nonexistent.invalid:443)
            """;

        var ex = Assert.Throws<PackageListException>(() => PackageListParser.Parse(output));

        Assert.Contains("did not return JSON", ex.Message, StringComparison.Ordinal);
        Assert.Equal(output, ex.RawOutput);
    }

    [Fact]
    public void Parse_ThrowsOnErrorProblems()
    {
        const string output = """
            { "version": 1, "problems": [ { "level": "error", "text": "No assets file was found for /repo/a.csproj." } ], "projects": [] }
            """;

        var ex = Assert.Throws<PackageListException>(() => PackageListParser.Parse(output));

        Assert.Contains("No assets file", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_IgnoresWarningProblems()
    {
        const string output = """
            { "version": 1, "problems": [ { "level": "warning", "text": "something minor" } ], "projects": [] }
            """;

        Assert.Empty(PackageListParser.Parse(output));
    }
}
