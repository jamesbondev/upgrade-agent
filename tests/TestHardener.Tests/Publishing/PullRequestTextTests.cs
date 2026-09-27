using TestHardener.Analysis;
using TestHardener.Hardening;
using TestHardener.Publishing;
using TestHardener.Stryker;
using TestHardener.Tests.TestSupport;

namespace TestHardener.Tests.Publishing;

public class PullRequestTextTests
{
    [Fact]
    public void Title_OneMethod_NamesIt() =>
        Assert.Equal("Add 2 tests that catch surviving mutants in Calculator.Add", PullRequestText.Title([Group("Calculator.Add(int, int)", tests: 2)]));

    [Fact]
    public void Title_SeveralMethods_CountsThem() =>
        Assert.Equal("Add 3 tests that catch surviving mutants in 2 methods",
            PullRequestText.Title([Group("Calculator.Add(int, int)", tests: 2), Group("Calculator.Limit.get", tests: 1)]));

    [Fact]
    public void CommitMessage_UsesTheScopes()
    {
        var message = PullRequestText.CommitMessage([Group("Calculator.Add(int, int)", tests: 1)], ["engine", null, "engine", "api"]);

        Assert.StartsWith("test(api,engine): cover surviving mutants", message, StringComparison.Ordinal);
        Assert.Contains("- Calculator.Add: 1 mutants caught by 1 tests", message, StringComparison.Ordinal);
    }

    [Fact]
    public void CommitMessage_WithoutScopes_IsPlainTest() =>
        Assert.StartsWith("test: cover surviving mutants", PullRequestText.CommitMessage([Group("A.B()", tests: 1)], [null]), StringComparison.Ordinal);

    [Fact]
    public void Description_EscapesTheAgentsTextAndWarnsTheReviewer()
    {
        var group = Group("Calculator.Add(int, int)", tests: 1) with
        {
            Summary = new GroupSummary { Tests = [new TestClaim { Name = "Add_Works", Asserts = "ping @team and *bold*" }], BlockedBy = BlockedBy.None },
        };

        var description = PullRequestText.Description("0123456789abcdef", [group], [new FileScore("src/A.cs", 6, 10, 2)], originalRuns: 7);

        Assert.Contains("commit `0123456789ab`", description, StringComparison.Ordinal);
        Assert.Contains("ping @​team and \\*bold\\*", description, StringComparison.Ordinal);
        Assert.Contains("Read each assertion", description, StringComparison.Ordinal);
        Assert.Contains("pass on the current code 7 times in a row", description, StringComparison.Ordinal);
        Assert.Contains("| `Calculator.Add` | `Add_Works` | 1 |", description, StringComparison.Ordinal);
        Assert.Contains("`src/A.cs`: 60.0% → 80.0%", description, StringComparison.Ordinal);
    }

    [Fact]
    public void FileScore_CountsDetectedOverValidAndAddsTheNewKills()
    {
        var report = new ReportBuilder("/clone")
            .Mutant(Samples.CalculatorPath, Samples.Calculator, "a > b", MutantStatus.Killed)
            .Mutant(Samples.CalculatorPath, Samples.Calculator, "n * 2", MutantStatus.Survived)
            .Mutant(Samples.CalculatorPath, Samples.Calculator, "10 - 1", MutantStatus.NoCoverage)
            .Mutant(Samples.CalculatorPath, Samples.Calculator, "seed > 0", MutantStatus.CompileError)
            .Build();
        var kept = Group("Calculator.Add(int, int)", tests: 1) with { Group = Group("x", 1).Group with { File = Samples.CalculatorPath } };

        var score = Assert.Single(FileScore.From([report], [kept]));

        Assert.Equal(1, score.Detected);
        Assert.Equal(3, score.Valid);
        Assert.Equal(1, score.NewlyKilled);
    }

    private static GroupResult Group(string member, int tests)
    {
        var testNames = Enumerable.Range(1, tests).Select(i => $"Demo.Tests.T.Add_Works{(i == 1 ? "" : i.ToString(System.Globalization.CultureInfo.InvariantCulture))}").ToList();
        return new GroupResult
        {
            Number = 1,
            Target = "Demo",
            Outcome = GroupOutcome.Verified,
            Group = new SurvivorGroup { File = "src/A.cs", Member = new MemberInfo(member, "method", 1, 2, 0, 1, false, "Calculator"), Survivors = [] },
            Rounds =
            [
                new Verification
                {
                    OwnedPath = "t.cs",
                    Passed = true,
                    NewTests = testNames.Select(n => new NewTest(n, n[(n.LastIndexOf('.') + 1)..], false)).ToList(),
                    Survivors = [new SurvivorOutcome("1", "Equality mutation", 1, Verification.KilledStatus, [testNames[0]])],
                    KillsPerTest = testNames.ToDictionary(n => n, _ => 1),
                },
            ],
        };
    }
}
