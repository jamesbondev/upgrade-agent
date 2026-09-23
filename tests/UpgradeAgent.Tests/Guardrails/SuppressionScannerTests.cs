using UpgradeAgent.Guardrails;

namespace UpgradeAgent.Tests.Guardrails;

public class SuppressionScannerTests
{
    [Theory]
    [InlineData("#pragma warning disable CS0618")]
    [InlineData("#nullable disable")]
    [InlineData("#if false")]
    [InlineData("<NoWarn>$(NoWarn);CS0618</NoWarn>")]
    [InlineData("<WarningsNotAsErrors>CS0618</WarningsNotAsErrors>")]
    [InlineData("<TreatWarningsAsErrors>false</TreatWarningsAsErrors>")]
    [InlineData("[SuppressMessage(\"Usage\", \"CA2000\")]")]
    [InlineData("[Fact(Skip = \"flaky after upgrade\")]")]
    [InlineData("[Fact(Skip=\"x\")]")]
    [InlineData("[Ignore]")]
    [InlineData("[Explicit]")]
    [InlineData("Assert.Skip(\"later\");")]
    [InlineData("<Compile Remove=\"Broken.cs\" />")]
    [InlineData("dotnet_diagnostic.CS0618.severity = none")]
    public void FlagsNewSuppressions(string line)
    {
        var violations = SuppressionScanner.Scan([new FileDiff("x", [line], [], false, false)]);

        Assert.Single(violations);
    }

    [Theory]
    [InlineData("var skipped = items.Skip(2);")]
    [InlineData("public int SkipCount { get; set; }")]
    [InlineData("// Assert.Equal(1, value) was here")]
    [InlineData("await client.GetConfigAsync(cancellationToken);")]
    public void IgnoresOrdinaryCode(string line)
    {
        Assert.Empty(SuppressionScanner.Scan([new FileDiff("x", [line], [], false, false)]));
    }

    [Fact]
    public void IgnoresMovedOrReindentedLines()
    {
        var diff = new FileDiff("App.csproj", ["    <NoWarn>CS1591</NoWarn>"], ["  <NoWarn>CS1591</NoWarn>"], false, false);

        Assert.Empty(SuppressionScanner.Scan([diff]));
    }

    [Fact]
    public void ADuplicatedSuppressionStillCounts()
    {
        var diff = new FileDiff("a.cs", ["#pragma warning disable CS0618", "#pragma warning disable CS0618"], ["#pragma warning disable CS0618"], false, false);

        Assert.Single(SuppressionScanner.Scan([diff]));
    }

    [Fact]
    public void FlagsNewGlobalSuppressionsFile()
    {
        var violations = SuppressionScanner.Scan([new FileDiff("src/App/GlobalSuppressions.cs", ["// empty"], [], IsNew: true, IsDeleted: false)]);

        Assert.Equal("new GlobalSuppressions.cs", Assert.Single(violations).Rule);
    }

    [Fact]
    public void ParsesGitDiffOutput()
    {
        const string diff = """
            diff --git a/src/App/Code.cs b/src/App/Code.cs
            index 1111111..2222222 100644
            --- a/src/App/Code.cs
            +++ b/src/App/Code.cs
            @@ -3 +3 @@
            -        var x = ValueFormatter.Format(1m);
            +        var x = ValueFormatter.FormatValue(1m);
            diff --git a/tests/Old.cs b/tests/Old.cs
            deleted file mode 100644
            index 3333333..0000000
            --- a/tests/Old.cs
            +++ /dev/null
            @@ -1 +0,0 @@
            -class Old {}
            """;

        var files = UnifiedDiff.Parse(diff);

        Assert.Equal(2, files.Count);
        Assert.Equal(("src/App/Code.cs", false), (files[0].Path, files[0].IsDeleted));
        Assert.Equal(["        var x = ValueFormatter.FormatValue(1m);"], files[0].Added);
        Assert.Equal(("tests/Old.cs", true), (files[1].Path, files[1].IsDeleted));
    }
}
