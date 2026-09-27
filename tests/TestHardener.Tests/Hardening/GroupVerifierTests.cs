using TestHardener.Analysis;
using TestHardener.Config;
using TestHardener.Hardening;
using TestHardener.Stryker;
using TestHardener.Tests.TestSupport;

namespace TestHardener.Tests.Hardening;

public class GroupVerifierTests
{
    private static readonly TargetConfig Target = new("Demo", "src/Demo/Demo.csproj", ["tests/Demo.Tests/Demo.Tests.csproj"], null, [], []);

    [Fact]
    public void Filter_JoinsDistinctTestNames() =>
        Assert.Equal(
            "FullyQualifiedName=A.T.One|FullyQualifiedName=A.T.Two",
            GroupVerifier.Filter([new NewTest("A.T.One", "One", false), new NewTest("A.T.Two", "Two", false), new NewTest("A.T.One", "One", true)]));

    [Fact]
    public void MutateSpan_IsRelativeToTheProjectFolder()
    {
        var group = new SurvivorGroup
        {
            File = "src/Demo/Sub/Calculator.cs",
            Member = new MemberInfo("Calculator.Add(int, int)", "method", 3, 9, 120, 480, false, "Calculator"),
            Survivors = [],
        };

        Assert.Equal("**/Sub/Calculator.cs{120..480}", GroupVerifier.MutateSpan(group, Target));
    }

    [Fact]
    public void Match_CountsKillsOnlyByNewTests()
    {
        var targeted = new[]
        {
            Survivor("a > b", "a >= b"),
            Survivor("a > b", "a < b"),
            Survivor("n * 2", "n / 2", "Arithmetic mutation"),
            Survivor("10 - 1", "10 + 1", "Arithmetic mutation"),
            Survivor("seed > 0", "seed >= 0"),
        };
        var report = new ReportBuilder("/clone")
            .Test("t1", "Demo.Tests.CalculatorTests.Add_New_Kills")
            .Test("t2", "Demo.Tests.CalculatorTests.Existing")
            .Test("t3", "Demo.Tests.CalculatorTests.Idle")
            .Mutant(Samples.CalculatorPath, Samples.Calculator, "a > b", MutantStatus.Killed, replacement: "a >= b")
            .Mutant(Samples.CalculatorPath, Samples.Calculator, "a > b", MutantStatus.Survived, replacement: "a < b")
            .Mutant(Samples.CalculatorPath, Samples.Calculator, "n * 2", MutantStatus.NoCoverage, "Arithmetic mutation", "n / 2")
            .Mutant(Samples.CalculatorPath, Samples.Calculator, "10 - 1", MutantStatus.Killed, "Arithmetic mutation", "10 + 1")
            .Json();
        var parsed = WithKilledBy(MutationReportParser.Parse(report, "/clone"), ("a >= b", ["t1"]), ("10 + 1", ["t2"]));
        NewTest[] newTests = [new("Demo.Tests.CalculatorTests.Add_New_Kills", "Add_New_Kills", false), new("Demo.Tests.CalculatorTests.Idle", "Idle", false)];

        var (outcomes, kills) = GroupVerifier.Match(targeted, Samples.CalculatorPath, parsed, newTests);

        Assert.Equal(["Killed", "Survived", "NotReached", "KilledByOtherTests", "Unmatched"], outcomes.Select(o => o.Status));
        Assert.Equal(1, kills["Demo.Tests.CalculatorTests.Add_New_Kills"]);
        Assert.Equal(0, kills["Demo.Tests.CalculatorTests.Idle"]);
    }

    [Fact]
    public void ParseTrx_CountsOutcomesAndMessages()
    {
        const string trx = """
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results>
                <UnitTestResult testName="A.T.One" outcome="Passed" />
                <UnitTestResult testName="A.T.Two" outcome="Failed">
                  <Output><ErrorInfo><Message>Assert.Equal() Failure</Message></ErrorInfo></Output>
                </UnitTestResult>
              </Results>
            </TestRun>
            """;

        var outcome = DotnetCli.ParseTrx([trx]);

        Assert.Equal(2, outcome.Total);
        Assert.Equal(1, outcome.Passed);
        Assert.Equal([new TestFailure("A.T.Two", "Assert.Equal() Failure")], outcome.Failures);
        Assert.False(outcome.Succeeded);
    }

    [Fact]
    public void ParseTrx_NotExecutedIsSkippedNotFailed()
    {
        const string trx = """
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>
            <UnitTestResult testName="A.T.One" outcome="Passed" />
            <UnitTestResult testName="A.T.Skipped" outcome="NotExecuted" />
            </Results></TestRun>
            """;

        var outcome = DotnetCli.ParseTrx([trx]);

        Assert.True(outcome.Succeeded);
        Assert.Equal(1, outcome.Skipped);
        Assert.Empty(outcome.Failures);
    }

    [Fact]
    public void OriginalFailure_TheoryRowsCountAsRunning() =>
        Assert.Null(GroupVerifier.OriginalFailure(
            new TestOutcome(true, 2, 0, 0, ["A.T.Rows(x: 1)", "A.T.Rows(x: 2)"], [], ""), 1, [new NewTest("A.T.Rows", "Rows", true)]));

    [Fact]
    public void OriginalFailure_TimeoutSaysItHangs() =>
        Assert.Contains("hangs", GroupVerifier.OriginalFailure(new TestOutcome(false, 0, 0, 0, [], [], "", TimedOut: true), 1, []), StringComparison.Ordinal);

    [Fact]
    public void ParseTrx_NoResults_IsNotASuccess() =>
        Assert.False(DotnetCli.ParseTrx(["<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\" />"]).Succeeded);

    private static Survivor Survivor(string snippet, string replacement, string mutator = "Equality mutation") =>
        new("x", mutator, SourceLocation.Of(Samples.Calculator, snippet), snippet, replacement, false, []);

    private static MutationReport WithKilledBy(MutationReport report, params (string Replacement, string[] Tests)[] killers) =>
        report with
        {
            Mutants = report.Mutants.Select(m => killers.FirstOrDefault(k => k.Replacement == m.Replacement) is { Tests: { } tests } ? m with { KilledBy = tests } : m).ToList(),
        };
}
