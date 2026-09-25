using UpgradeAgent.Bumping;
using UpgradeAgent.Run;
using UpgradeAgent.Tests.TestSupport;

namespace UpgradeAgent.Tests.Run;

public class CommitMessageTests
{
    [Fact]
    public void ListsUpToThreePackagesInTheSubject()
    {
        var message = CommitMessage.Create("Fixture.Lib", [TestData.Edit("Fixture.Lib", "1.1.0", "2.0.0")], "run-1");

        Assert.StartsWith("chore(deps): bump Fixture.Lib 1.1.0 -> 2.0.0\n", message.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void SummarisesLargerGroupsAndCollapsesPerProjectEdits()
    {
        VersionEdit[] edits =
        [
            TestData.Edit("A", "1.0.0", "1.1.0", "a/A.csproj"), TestData.Edit("A", "1.0.0", "1.1.0", "b/B.csproj"),
            TestData.Edit("B", "1.0.0", "1.1.0"), TestData.Edit("C", "1.0.0", "1.1.0"), TestData.Edit("D", "1.0.0", "1.0.1"),
        ];

        var lines = CommitMessage.Create("patch-minor", edits, "run-1").ReplaceLineEndings("\n").Split('\n');

        Assert.Equal("chore(deps): bump 4 packages (patch-minor)", lines[0]);
        Assert.Equal(4, lines.Count(l => l.StartsWith("- ", StringComparison.Ordinal)));
    }
}
