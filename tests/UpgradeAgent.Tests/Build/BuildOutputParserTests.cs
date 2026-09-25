using UpgradeAgent.Build;

namespace UpgradeAgent.Tests.Build;

public class BuildOutputParserTests
{
    [Fact]
    public void ParsesAndDeduplicatesCompilerDiagnostics()
    {
        // Captured shape: each diagnostic is repeated with the project suffix.
        const string Output = """
            /repo/src/LoanLedger/InterestCalculator.cs(10,36): error CS1061: 'ConfigClient' does not contain a definition for 'GetConfig' [/repo/src/LoanLedger/LoanLedger.csproj]
            /repo/src/LoanLedger/InterestCalculator.cs(10,36): error CS1061: 'ConfigClient' does not contain a definition for 'GetConfig' [/repo/src/LoanLedger/LoanLedger.csproj]
            /repo/src/LoanLedger/StatementService.cs(19,28): warning CS0618: 'ValueFormatter.Format(decimal)' is obsolete [/repo/src/LoanLedger/LoanLedger.csproj]
            Build FAILED.
            """;

        var (errors, warnings) = BuildOutputParser.ParseDiagnostics(Output);

        var error = Assert.Single(errors);
        Assert.Equal(("CS1061", 10, "/repo/src/LoanLedger/InterestCalculator.cs"), (error.Code, error.Line, error.File));
        Assert.Equal("'ConfigClient' does not contain a definition for 'GetConfig'", error.Message);
        Assert.Equal("CS0618", Assert.Single(warnings).Code);
    }

    [Fact]
    public void ParsesDiagnosticsWithoutALineNumber()
    {
        const string Output = "/repo/src/App/App.csproj : error NU1202: Package Foo 10.0.0 is not compatible with net8.0 (.NETCoreApp,Version=v8.0). [/repo/App.slnx]";

        var error = Assert.Single(BuildOutputParser.ParseDiagnostics(Output).Errors);

        Assert.Equal(("NU1202", "/repo/src/App/App.csproj", (int?)null), (error.Code, error.File, error.Line));
    }

    [Fact]
    public void ToolDiagnosticsHaveAnOriginNotAFile()
    {
        var error = Assert.Single(BuildOutputParser.ParseDiagnostics("CSC : error CS2001: Source file 'Gone.cs' could not be found.").Errors);

        Assert.Equal(((string?)null, "CSC"), (error.File, error.Origin));
    }

    [Theory]
    [InlineData("Build FAILED.\n\n    0 Warning(s)\n    12 Error(s)\n", 12)]
    [InlineData("Build succeeded.\n    0 Warning(s)\n    0 Error(s)\n", 0)]
    [InlineData("Build failed with 3 error(s) and 1 warning(s) in 4.2s", 3)]
    [InlineData("Build succeeded in 3.1s", 0)]
    [InlineData("src/A.cs(3,5): error CS0117: 'X' does not contain 'Y' [/wt/src/A.csproj]\nBuild FAILED.", 1)]
    [InlineData("src/A.cs(3,5): error CS0117: 'X' does not contain 'Y' [/wt/src/A.csproj]", null)]
    [InlineData("", null)]
    public void CountsErrorsOnlyWhenTheOutputSaysHowTheBuildEnded(string output, int? expected)
    {
        Assert.Equal(expected, BuildOutputParser.CountErrors(output));
    }

    [Fact]
    public void SumsVsTestSummaries()
    {
        const string Output = """
            Passed!  - Failed:     0, Passed:    16, Skipped:     0, Total:    16, Duration: 72 ms - LoanLedger.Tests.dll (net10.0)
            Failed!  - Failed:     2, Passed:     5, Skipped:     1, Total:     8, Duration: 10 ms - Other.Tests.dll (net10.0)
            """;

        Assert.Equal(new TestCounts(24, 21, 2, 1), BuildOutputParser.ParseTestCounts(Output));
    }
}
