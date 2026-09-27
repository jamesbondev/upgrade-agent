using TestHardener.Stryker;
using TestHardener.Tests.TestSupport;

namespace TestHardener.Tests.Stryker;

public class MutationReportParserTests
{
    [Fact]
    public void Parse_ReadsMutantsWithRepoRelativePaths()
    {
        var report = new ReportBuilder("/clone/demo")
            .Test("t1", "Demo.Tests.CalculatorTests.Add_Works")
            .Mutant(Samples.CalculatorPath, Samples.Calculator, "a > b", MutantStatus.Survived, replacement: "a >= b", coveredBy: ["t1"])
            .Build();

        var mutant = Assert.Single(report.Mutants);
        Assert.Equal(Samples.CalculatorPath, mutant.File);
        Assert.Equal("a >= b", mutant.Replacement);
        Assert.Equal(MutantStatus.Survived, mutant.Status);
        Assert.Equal(["t1"], mutant.CoveredBy);
        Assert.Equal("Demo.Tests.CalculatorTests.Add_Works", report.TestNames["t1"]);
        Assert.Equal(1, report.TestCount);
    }

    [Fact]
    public void Key_TellsApartMutantsAtTheSameLocation()
    {
        var report = new ReportBuilder("/clone/demo")
            .Mutant(Samples.CalculatorPath, Samples.Calculator, "a > b", MutantStatus.Survived, replacement: "a >= b")
            .Mutant(Samples.CalculatorPath, Samples.Calculator, "a > b", MutantStatus.Survived, replacement: "a < b")
            .Build();

        Assert.Equal(report.Mutants[0].Location, report.Mutants[1].Location);
        Assert.NotEqual(report.Mutants[0].Key, report.Mutants[1].Key);
    }

    [Fact]
    public void Score_CountsTimeoutsAsDetectedAndIgnoresCompileErrors()
    {
        var report = new ReportBuilder("/r")
            .Mutant(Samples.CalculatorPath, Samples.Calculator, "a > b", MutantStatus.Killed)
            .Mutant(Samples.CalculatorPath, Samples.Calculator, "n * 2", MutantStatus.Timeout)
            .Mutant(Samples.CalculatorPath, Samples.Calculator, "10 - 1", MutantStatus.Survived)
            .Mutant(Samples.CalculatorPath, Samples.Calculator, "seed > 0", MutantStatus.NoCoverage)
            .Mutant(Samples.CalculatorPath, Samples.Calculator, "value + 1", MutantStatus.CompileError)
            .Build();

        Assert.Equal(50.0, report.Score);
    }

    [Theory]
    [InlineData("/clone/demo/src/A.cs", "/clone/demo", "src/A.cs")]
    [InlineData("/clone/demo/src/A.cs", "/clone/demo/", "src/A.cs")]
    [InlineData("C:\\clone\\demo\\src\\A.cs", "C:\\clone\\demo", "src/A.cs")]
    [InlineData("/elsewhere/src/A.cs", "/clone/demo", "elsewhere/src/A.cs")]
    public void RelativePath_StripsTheCloneRoot(string reportPath, string root, string expected) =>
        Assert.Equal(expected, MutationReportParser.RelativePath(reportPath, root));

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{ "files": { "a.cs": { "mutants": [ { "id": "1" } ] } } }""")]
    public void Parse_BrokenReport_Throws(string json) =>
        Assert.Throws<MutationReportException>(() => MutationReportParser.Parse(json, "/r"));
}
