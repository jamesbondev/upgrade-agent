using ReadmeChecker.Agent;
using ReadmeChecker.Tests.TestSupport;

namespace ReadmeChecker.Tests.Agent;

public class AssessmentValidatorTests
{
    private static readonly ReadmeChecker.Detection.RepoFacts Facts = FactsBuilder.Readme(
        "# Demo\r\n\r\nRun\r\n   `dotnet run --project src/Old`\r\nto start.\r\n",
        ["src/New/New.csproj", "src/New/Program.cs"]);

    [Fact]
    public void AVerbatimQuoteWithExistingEvidenceIsAccepted()
    {
        var validation = AssessmentValidator.Validate(Assessment(Issue("dotnet run --project src/Old", "./src/New/New.csproj", "src\\New")), Facts);

        var issue = Assert.Single(validation.Accepted);
        Assert.Equal(["src/New/New.csproj", "src/New"], issue.Evidence);
        Assert.Empty(validation.Rejected);
    }

    [Fact]
    public void QuotesMatchAcrossLineEndingsAndWhitespace()
    {
        var validation = AssessmentValidator.Validate(Assessment(Issue("Run `dotnet run --project src/Old` to start.", "src/New")), Facts);

        Assert.Single(validation.Accepted);
    }

    [Theory]
    [InlineData("dotnet run --project src/Api", "the quote isn't in the README")]
    [InlineData("  ", "the quote is empty")]
    public void AQuoteThatIsntInTheReadmeIsRejected(string quote, string reason)
    {
        var validation = AssessmentValidator.Validate(Assessment(Issue(quote, "src/New")), Facts);

        Assert.Equal(reason, Assert.Single(validation.Rejected).Reason);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\secrets.txt")]
    [InlineData("src/Missing.cs")]
    public void EvidenceMustBeInTheRepository(string evidence)
    {
        var validation = AssessmentValidator.Validate(Assessment(Issue("dotnet run --project src/Old", evidence)), Facts);

        Assert.StartsWith("evidence not in the repository", Assert.Single(validation.Rejected).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingContentNeedsEvidenceButABrokenReferenceDoesNot()
    {
        var missing = Issue("# Demo") with { Kind = IssueKind.MissingContent };
        var broken = Issue("src/Old") with { Kind = IssueKind.BrokenReference };

        var validation = AssessmentValidator.Validate(Assessment(missing, broken), Facts);

        Assert.Equal(IssueKind.BrokenReference, Assert.Single(validation.Accepted).Kind);
        Assert.Equal("no evidence from the repository", Assert.Single(validation.Rejected).Reason);
    }

    [Fact]
    public void OnlyTheFirstIssuesAreKept()
    {
        var issues = Enumerable.Range(0, AssessmentValidator.MaxIssues + 2).Select(_ => Issue("src/Old", "src/New")).ToArray();

        var validation = AssessmentValidator.Validate(Assessment(issues), Facts);

        Assert.Equal(AssessmentValidator.MaxIssues, validation.Accepted.Count);
        Assert.All(validation.Rejected, r => Assert.Contains("more than", r.Reason, StringComparison.Ordinal));
    }

    private static ReadmeAssessment Assessment(params ReadmeIssue[] issues) =>
        new() { Verdict = AssessedVerdict.Stale, Issues = issues, Summary = "summary" };

    private static ReadmeIssue Issue(string quote, params string[] evidence) =>
        new() { Kind = IssueKind.WrongCommand, Quote = quote, Evidence = evidence, SuggestedFix = "fix it" };
}
