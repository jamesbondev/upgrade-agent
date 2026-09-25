using ReadmeChecker.Agent;
using ReadmeChecker.Detection;
using ReadmeChecker.Tests.TestSupport;
using RepoKit;

namespace ReadmeChecker.Tests.Agent;

public class AssessmentValidatorTests
{
    private static readonly RepoFacts Facts = FactsBuilder.Readme(
        "# Demo\r\n\r\nRun\r\n   `dotnet run --project src/Old`\r\nto start.\r\n",
        ["src/New/New.csproj", "src/New/Program.cs"]);

    [Fact]
    public async Task AVerbatimQuoteWithExistingEvidenceIsAccepted()
    {
        var validation = await Validate(Assessment(Issue("dotnet run --project src/Old", "./src/New/New.csproj", "src\\New")), Facts, []);

        var issue = Assert.Single(validation.Accepted);
        Assert.Equal(["src/New/New.csproj", "src/New"], issue.Evidence);
        Assert.Empty(validation.Rejected);
    }

    [Fact]
    public async Task QuotesMatchAcrossLineEndingsAndWhitespace()
    {
        var validation = await Validate(Assessment(Issue("Run `dotnet run --project src/Old` to start.", "src/New")), Facts, []);

        Assert.Single(validation.Accepted);
    }

    [Theory]
    [InlineData("dotnet run --project src/Api", "the quote isn't in the README")]
    [InlineData("  ", "the quote is empty")]
    public async Task AQuoteThatIsntInTheReadmeIsRejected(string quote, string reason)
    {
        var validation = await Validate(Assessment(Issue(quote, "src/New")), Facts, []);

        Assert.Equal(reason, Assert.Single(validation.Rejected).Reason);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("/etc/passwd")]
    [InlineData("C:\\secrets.txt")]
    [InlineData("src/Missing.cs")]
    public async Task EvidenceMustBeInTheRepository(string evidence)
    {
        var validation = await Validate(Assessment(Issue("dotnet run --project src/Old", evidence)), Facts, []);

        Assert.StartsWith("evidence not in the repository", Assert.Single(validation.Rejected).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingContentNeedsEvidenceButABrokenReferenceTheScriptFoundDoesNot()
    {
        var missing = Issue("# Demo") with { Kind = IssueKind.MissingContent };
        var broken = Issue("src/Old") with { Kind = IssueKind.BrokenReference };
        var signal = new Signal(SignalKind.MissingCommandTarget, 4, "src/Old", "gone", "src/Old");

        var validation = await Validate(Assessment(missing, broken), Facts, [signal]);

        Assert.Equal(IssueKind.BrokenReference, Assert.Single(validation.Accepted).Kind);
        Assert.Equal("no evidence from the repository", Assert.Single(validation.Rejected).Reason);
    }

    [Fact]
    public async Task ABareFileNameIsNotEnoughForABrokenReference()
    {
        var facts = FactsBuilder.Readme("A repository's `AGENTS.md` is read as well.", ["src/a.cs"]);
        var broken = Issue("A repository's `AGENTS.md` is read as well.") with { Kind = IssueKind.BrokenReference };
        var signal = new Signal(SignalKind.MissingPath, 1, "AGENTS.md", "'AGENTS.md' is not in the repository", "AGENTS.md");

        var validation = await Validate(Assessment(broken), facts, [signal]);

        Assert.StartsWith("a broken reference needs evidence", Assert.Single(validation.Rejected).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABrokenReferenceWithNoMatchingSignalIsRejected()
    {
        var broken = Issue("src/Old") with { Kind = IssueKind.BrokenReference };

        Assert.Single((await Validate(Assessment(broken), Facts, [])).Rejected);
    }

    [Fact]
    public async Task OnlyTheFirstIssuesAreKept()
    {
        var facts = FactsBuilder.Readme(string.Join('\n', Enumerable.Range(0, 40).Select(i => $"claim number {i} here")));
        var issues = Enumerable.Range(0, AssessmentValidator.MaxIssues + 2)
            .Select(i => new ReadmeIssue { Kind = IssueKind.Other, Quote = $"claim number {i} here", SuggestedFix = "fix it" })
            .ToArray();

        var validation = await Validate(Assessment(issues), facts, []);

        Assert.Equal(AssessmentValidator.MaxIssues, validation.Accepted.Count);
        Assert.All(validation.Rejected, r => Assert.Contains("more than", r.Reason, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheSameQuoteAndKindIsReportedOnce()
    {
        var validation = await Validate(Assessment(Issue("src/Old", "src/New"), Issue("src/Old", "src/New/New.csproj")), Facts, []);

        Assert.Single(validation.Accepted);
    }

    private static Task<IssueValidation> Validate(ReadmeAssessment assessment, RepoFacts facts, IReadOnlyList<Signal> signals) =>
        AssessmentValidator.ValidateAsync(assessment.Issues, new ValidationScope(Path.GetTempPath(), new GitCli(new ProcessRunner()), facts, signals), CancellationToken.None);

    private static ReadmeAssessment Assessment(params ReadmeIssue[] issues) =>
        new() { Verdict = AssessedVerdict.Stale, Issues = issues, Summary = "summary" };

    private static ReadmeIssue Issue(string quote, params string[] evidence) =>
        new() { Kind = IssueKind.WrongCommand, Quote = quote, Evidence = evidence, SuggestedFix = "fix it" };
}
