using TestHardener.Analysis;
using TestHardener.Reporting;
using TestHardener.Run;
using TestHardener.Stryker;

namespace TestHardener.Tests.Reporting;

public class SurveyReportWriterTests
{
    [Fact]
    public void Change_ShortTexts_AreShownWhole() =>
        Assert.Equal("`a > b` → `a >= b`", SurveyReportWriter.Change("a > b", "a >= b"));

    [Fact]
    public void Change_LongTexts_ShowTheDifferingPart()
    {
        var head = new string('x', 100);
        var shown = SurveyReportWriter.Change($"{head}.OrderBy(f => f.Path).ToList()", $"{head}.OrderByDescending(f => f.Path).ToList()");

        Assert.Contains("OrderBy(", shown, StringComparison.Ordinal);
        Assert.Contains("OrderByDescending(", shown, StringComparison.Ordinal);
        Assert.StartsWith("`…", shown, StringComparison.Ordinal);
    }

    [Fact]
    public void Change_CollapsesWhitespaceAndBackticks() =>
        Assert.Equal("`a . b` → `'c'`", SurveyReportWriter.Change("a\n     . b", "`c`"));

    [Fact]
    public void Markdown_ListsTheNextGroupsAndFailures()
    {
        var group = new SurvivorGroup
        {
            Rank = 1,
            File = "src/A.cs",
            Member = new MemberInfo("A.Run(int)", "method", 3, 9, 10, 90, IsLogging: false),
            Survivors = [new Survivor("1", "Equality mutation", new Location(new(4, 5), new(4, 10)), "a > b", "a >= b", false, ["T.A"])],
            CoveringTests = ["T.A"],
            Reasons = ["1 surviving mutant"],
        };
        var report = new SurveyReport("run1", DateTimeOffset.UnixEpoch, TimeSpan.FromMinutes(2), "/out", null,
        [
            new RepoSurvey
            {
                Name = "demo",
                Location = "/repos/demo",
                Status = SurveyStatus.Partial,
                Sha = "abc123",
                Targets =
                [
                    new TargetSurvey
                    {
                        Name = "A",
                        Project = "src/A.csproj",
                        StatusCounts = new Dictionary<MutantStatus, int> { [MutantStatus.Survived] = 1, [MutantStatus.Killed] = 3 },
                        Score = 75,
                        Tests = 4,
                        Survivors = 1,
                        Groups = [group],
                    },
                    new TargetSurvey { Name = "B", Project = "src/B.csproj", Failure = "Stryker exited with code 1" },
                ],
            },
        ]);

        var markdown = SurveyReportWriter.Markdown(report, nextGroups: 5);

        Assert.Contains("| demo | A | 75.0% | 4 | 1 | 1 | 1 |", markdown, StringComparison.Ordinal);
        Assert.Contains("1. **A.Run(int)** in src/A.cs, lines 3-9", markdown, StringComparison.Ordinal);
        Assert.Contains("line 4, Equality mutation: `a > b` → `a >= b`", markdown, StringComparison.Ordinal);
        Assert.Contains("Failed: Stryker exited with code 1", markdown, StringComparison.Ordinal);
        Assert.Contains("at commit `abc123`", markdown, StringComparison.Ordinal);
    }
}
