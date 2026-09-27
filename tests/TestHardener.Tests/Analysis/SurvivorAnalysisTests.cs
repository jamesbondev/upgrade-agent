using TestHardener.Analysis;
using TestHardener.Config;
using TestHardener.Stryker;
using TestHardener.Tests.TestSupport;

namespace TestHardener.Tests.Analysis;

public class SurvivorAnalysisTests
{
    private const string Root = "/clone/demo";

    private static readonly TargetConfig Target = new("Demo", "src/Demo/Demo.csproj", ["tests/Demo.Tests/Demo.Tests.csproj"], null, [], []);

    [Fact]
    public void Analyze_GroupsSurvivorsByMember_AndSkipsNoise()
    {
        var report = Builder()
            .Mutant(Samples.CalculatorPath, Samples.Calculator, "a > b", MutantStatus.Survived, replacement: "a >= b")
            .Mutant(Samples.CalculatorPath, Samples.Calculator, "a > b", MutantStatus.Survived, replacement: "a < b")
            .Mutant(Samples.CalculatorPath, Samples.Calculator, "n * 2", MutantStatus.Survived, "Arithmetic mutation", "n / 2")
            .Mutant(Samples.CalculatorPath, Samples.Calculator, "\"alpha\"", MutantStatus.Survived, "String mutation", "\"\"", coveredBy: [], isStatic: true)
            .Mutant(Samples.CalculatorPath, Samples.Calculator, "\"bigger {A}\"", MutantStatus.Survived, "String mutation", "\"\"")
            .Mutant(Samples.CalculatorPath, Samples.Calculator, "value + 1", MutantStatus.Survived, "Arithmetic mutation", "value - 1")
            .Mutant(Samples.CalculatorPath, Samples.Calculator, "\"calc\"", MutantStatus.Survived, "String mutation", "\"\"")
            .Mutant(Samples.CalculatorPath, Samples.Calculator, "s.Length > 3", MutantStatus.Killed)
            .Build();

        var analysis = Analyze(report);

        Assert.Equal(["Calculator.Add(int, int)", "Calculator.Name.get"], analysis.Groups.Select(g => g.Member.Name));
        Assert.Equal(3, analysis.Groups[0].Survivors.Count);
        Assert.Equal(1, analysis.Skipped[SkipReason.StaticData]);
        Assert.Equal(2, analysis.Skipped[SkipReason.Logging]);
    }

    [Fact]
    public void Analyze_PutsStringMutationsLastWithinAGroup()
    {
        var source = Samples.Calculator.Replace("public string Name => \"calc\";", "public string Name => a > 1 ? \"calc\" : \"\";", StringComparison.Ordinal);
        var mixed = Builder()
            .Mutant(Samples.CalculatorPath, source, "\"calc\"", MutantStatus.Survived, "String mutation", "\"\"")
            .Mutant(Samples.CalculatorPath, source, "a > 1", MutantStatus.Survived, replacement: "a >= 1")
            .Build();

        var group = SurvivorAnalysis.Analyze(mixed, Target, _ => source, new Dictionary<string, int>()).Groups.Single();

        Assert.Equal(["Equality mutation", "String mutation"], group.Survivors.Select(s => s.Mutator));
    }

    [Fact]
    public void Analyze_IgnoredStringGlob_SkipsOnlyStringMutations()
    {
        var target = Target with { IgnoreStringMutationsIn = ["**/Demo/**"] };
        var report = Builder()
            .Mutant(Samples.CalculatorPath, Samples.Calculator, "\"calc\"", MutantStatus.Survived, "String mutation", "\"\"")
            .Mutant(Samples.CalculatorPath, Samples.Calculator, "a > b", MutantStatus.Survived)
            .Build();

        var analysis = SurvivorAnalysis.Analyze(report, target, _ => Samples.Calculator, new Dictionary<string, int>());

        Assert.Equal(1, analysis.Skipped[SkipReason.IgnoredString]);
        Assert.Equal("Calculator.Add(int, int)", analysis.Groups.Single().Member.Name);
    }

    [Fact]
    public void Analyze_RanksBySurvivorsThenFixCommits()
    {
        const string other = "src/Demo/Other.cs";
        var report = Builder()
            .Mutant(Samples.CalculatorPath, Samples.Calculator, "a > b", MutantStatus.Survived)
            .Mutant(other, Samples.Calculator, "s.Length > 3", MutantStatus.Survived)
            .Mutant(other, Samples.Calculator, "10 - 1", MutantStatus.Survived, "Arithmetic mutation")
            .Mutant(other, Samples.Calculator, "10 - 1", MutantStatus.Survived, "Arithmetic mutation", "10 + 1")
            .Build();

        var groups = SurvivorAnalysis.Analyze(report, Target, _ => Samples.Calculator, new Dictionary<string, int> { [Samples.CalculatorPath] = 3 }).Groups;

        Assert.Equal(["Calculator.Limit.get", "Calculator.Add(int, int)", "Calculator.Inner.Check(string)"], groups.Select(g => g.Member.Name));
        Assert.Equal([1, 2, 3], groups.Select(g => g.Rank));
        Assert.Contains("3 fix commits touched the file recently", groups[1].Reasons);
    }

    [Fact]
    public void Analyze_RanksGroupsWithLogicSurvivorsAboveStringOnlyGroups()
    {
        var report = Builder()
            .Mutant(Samples.CalculatorPath, Samples.Calculator, "\"calc\"", MutantStatus.Survived, "String mutation", "\"\"")
            .Mutant(Samples.CalculatorPath, Samples.Calculator, "\"calc\"", MutantStatus.Survived, "String mutation", "\"x\"")
            .Mutant(Samples.CalculatorPath, Samples.Calculator, "a > b", MutantStatus.Survived)
            .Build();

        Assert.Equal(["Calculator.Add(int, int)", "Calculator.Name.get"], Analyze(report).Groups.Select(g => g.Member.Name));
    }

    [Fact]
    public void Analyze_ListsCoveringTestNames()
    {
        var report = Builder()
            .Test("t1", "Demo.Tests.CalculatorTests.Add_Works")
            .Test("t2", "Demo.Tests.CalculatorTests.Add_Other")
            .Mutant(Samples.CalculatorPath, Samples.Calculator, "a > b", MutantStatus.Survived, coveredBy: ["t2", "t1"])
            .Build();

        var group = Analyze(report).Groups.Single();

        Assert.Equal(["Demo.Tests.CalculatorTests.Add_Other", "Demo.Tests.CalculatorTests.Add_Works"], group.CoveringTests);
    }

    [Fact]
    public void Analyze_MemberWhereEveryMutantIsACompileError_IsNotMutated()
    {
        var report = Builder()
            .Mutant(Samples.CalculatorPath, Samples.Calculator, "a > b", MutantStatus.CompileError)
            .Mutant(Samples.CalculatorPath, Samples.Calculator, "n * 2", MutantStatus.CompileError)
            .Mutant(Samples.CalculatorPath, Samples.Calculator, "seed > 0", MutantStatus.CompileError)
            .Mutant(Samples.CalculatorPath, Samples.Calculator, "seed > 0", MutantStatus.Killed, replacement: "y")
            .Build();

        var notMutated = Analyze(report).NotMutated;

        Assert.Equal([new NotMutatedMember(Samples.CalculatorPath, "Calculator.Add(int, int)", 2)], notMutated);
    }

    [Fact]
    public void Analyze_CountsUntestedMutantsPerFile()
    {
        var report = Builder()
            .Mutant(Samples.CalculatorPath, Samples.Calculator, "a > b", MutantStatus.NoCoverage)
            .Mutant(Samples.CalculatorPath, Samples.Calculator, "n * 2", MutantStatus.NoCoverage)
            .Build();

        Assert.Equal([new UntestedFile(Samples.CalculatorPath, 2)], Analyze(report).Untested);
    }

    [Fact]
    public void Analyze_MissingSourceFile_IsSkipped()
    {
        var report = Builder().Mutant(Samples.CalculatorPath, Samples.Calculator, "a > b", MutantStatus.Survived).Build();

        var analysis = SurvivorAnalysis.Analyze(report, Target, _ => null, new Dictionary<string, int>());

        Assert.Empty(analysis.Groups);
        Assert.Equal(1, analysis.Skipped[SkipReason.SourceMissing]);
    }

    private static ReportBuilder Builder() => new(Root);

    private static TargetAnalysis Analyze(MutationReport report) =>
        SurvivorAnalysis.Analyze(report, Target, _ => Samples.Calculator, new Dictionary<string, int>());
}
