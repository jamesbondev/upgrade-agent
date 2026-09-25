using ReadmeChecker.Agent;
using ReadmeChecker.Detection;
using ReadmeChecker.Publishing;

namespace ReadmeChecker.Tests.Publishing;

public class PullRequestTextTests
{
    private static readonly ReadmeIssue Issue = new()
    {
        Kind = IssueKind.WrongCommand,
        Quote = "dotnet run --project src/Old",
        Evidence = ["src/New"],
        SuggestedFix = "Use src/New. cc @alice <img src=x onerror=alert(1)>",
    };

    private static readonly Signal Link = new(SignalKind.BrokenLink, 4, "docs/setup.md", "gone", "docs/setup.md");

    [Fact]
    public void TheDescriptionListsTheProblemsAndSaysTheTextIsUnverified()
    {
        var description = PullRequestText.Description("README.md", [Issue], [Link], "- Fixed the run command.\n- Fixed the link.");

        Assert.Contains("unverified", description, StringComparison.Ordinal);
        Assert.Contains("1. **WrongCommand**: \"dotnet run --project src/Old\".", description, StringComparison.Ordinal);
        Assert.Contains("2. **Broken link** on line 4: docs/setup.md", description, StringComparison.Ordinal);
        Assert.Contains("- Fixed the link.", description, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentTextCantMentionPeopleOrInjectMarkup()
    {
        var description = PullRequestText.Description("README.md", [Issue], [], "ping @bob");

        Assert.DoesNotContain("@alice", description, StringComparison.Ordinal);
        Assert.DoesNotContain("@bob", description, StringComparison.Ordinal);
        Assert.Contains("\\<img", description, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCommitMessageSummarisesEachProblemOnOneLine()
    {
        var message = PullRequestText.CommitMessage("docs/README.md", [Issue], [Link]);

        Assert.StartsWith("docs: update docs/README.md to match the repository", message, StringComparison.Ordinal);
        Assert.Contains("- WrongCommand: Use src/New.", message, StringComparison.Ordinal);
        Assert.Contains("- Broken link: docs/setup.md", message, StringComparison.Ordinal);
    }
}
