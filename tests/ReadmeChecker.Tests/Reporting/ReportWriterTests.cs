using ReadmeChecker.Agent;
using ReadmeChecker.Reporting;
using ReadmeChecker.Run;

namespace ReadmeChecker.Tests.Reporting;

public class ReportWriterTests
{
    [Fact]
    public void TheMarkdownReportListsStaleReposFirstAndEscapesAgentText()
    {
        var stale = new RepoReport
        {
            Name = "api",
            Location = "https://dev.azure.com/contoso/Team/_git/api",
            Verdict = RepoVerdict.Stale,
            ReadmePath = "README.md",
            Issues = [new ReadmeIssue { Kind = IssueKind.WrongCommand, Quote = "run | <script>", Evidence = ["src/New"], SuggestedFix = "Use *new*." }],
        };
        var current = new RepoReport { Name = "web", Location = "/repos/web", Verdict = RepoVerdict.Current, Deterministic = true };
        var report = new CheckReport("20260925-120000", DateTimeOffset.UnixEpoch, TimeSpan.FromMinutes(2), "/out", 1.5, [current, stale]);

        var markdown = ReportWriter.Markdown(report);

        Assert.True(markdown.IndexOf("| api |", StringComparison.Ordinal) < markdown.IndexOf("| web |", StringComparison.Ordinal));
        Assert.Contains("Current (no agent)", markdown, StringComparison.Ordinal);
        Assert.Contains("run \\| \\<script\\>", markdown, StringComparison.Ordinal);
        Assert.Contains("Use \\*new\\*.", markdown, StringComparison.Ordinal);
        Assert.Contains("2 repos in 2.0 min: 1 stale, 1 current. AI credits: 1.5.", markdown, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("payments-api", "payments-api")]
    [InlineData("Team/My Repo", "Team-My-Repo")]
    [InlineData("..", "repo")]
    public void RepoFolderNamesAreSafe(string name, string expected)
    {
        Assert.Equal(expected, ReportWriter.FolderName(name));
    }
}
