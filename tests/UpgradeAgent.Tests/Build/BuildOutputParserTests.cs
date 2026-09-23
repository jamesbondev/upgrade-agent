using UpgradeAgent.Build;

namespace UpgradeAgent.Tests.Build;

public class BuildOutputParserTests
{
    [Fact]
    public void ParsesAndDeduplicatesCompilerDiagnostics()
    {
        // Captured shape: each diagnostic is repeated with the project suffix.
        const string output = """
            /repo/src/LoanLedger/InterestCalculator.cs(10,36): error CS1061: 'ConfigClient' does not contain a definition for 'GetConfig' [/repo/src/LoanLedger/LoanLedger.csproj]
            /repo/src/LoanLedger/InterestCalculator.cs(10,36): error CS1061: 'ConfigClient' does not contain a definition for 'GetConfig' [/repo/src/LoanLedger/LoanLedger.csproj]
            /repo/src/LoanLedger/StatementService.cs(19,28): warning CS0618: 'ValueFormatter.Format(decimal)' is obsolete [/repo/src/LoanLedger/LoanLedger.csproj]
            Build FAILED.
            """;

        var (errors, warnings) = BuildOutputParser.ParseDiagnostics(output);

        var error = Assert.Single(errors);
        Assert.Equal(("CS1061", 10, "/repo/src/LoanLedger/InterestCalculator.cs"), (error.Code, error.Line, error.File));
        Assert.Equal("'ConfigClient' does not contain a definition for 'GetConfig'", error.Message);
        Assert.Equal("CS0618", Assert.Single(warnings).Code);
    }

    [Fact]
    public void ParsesDiagnosticsWithoutALineNumber()
    {
        const string output = "/repo/src/App/App.csproj : error NU1202: Package Foo 10.0.0 is not compatible with net8.0 (.NETCoreApp,Version=v8.0). [/repo/App.slnx]";

        var error = Assert.Single(BuildOutputParser.ParseDiagnostics(output).Errors);

        Assert.Equal(("NU1202", "/repo/src/App/App.csproj", (int?)null), (error.Code, error.File, error.Line));
    }

    [Fact]
    public void SumsVsTestSummaries()
    {
        const string output = """
            Passed!  - Failed:     0, Passed:    16, Skipped:     0, Total:    16, Duration: 72 ms - LoanLedger.Tests.dll (net10.0)
            Failed!  - Failed:     2, Passed:     5, Skipped:     1, Total:     8, Duration: 10 ms - Other.Tests.dll (net10.0)
            """;

        Assert.Equal(new TestCounts(24, 21, 2, 1), BuildOutputParser.ParseTestCounts(output));
    }
}
