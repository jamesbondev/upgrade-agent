using ReadmeChecker.Agent;
using ReadmeChecker.Detection;
using ReadmeChecker.Tests.TestSupport;

namespace ReadmeChecker.Tests.Agent;

public sealed class DeepValidationTests : IAsyncLifetime, IDisposable
{
    private const string Readme = """
        # Demo

        Lenses use Claude Sonnet 4.6 by default.

        ## Concurrency

        PR locks and map locks keep reviews apart.
        """;

    private TempRepo _repo = null!;
    private RepoFacts _facts = null!;

    public async Task InitializeAsync()
    {
        _repo = await TempRepo.CreateAsync();
        await _repo
            .Write("README.md", Readme)
            .Write("src/App/appsettings.json", "{\n  \"Llm\": {\n    \"DefaultModel\": \"claude-sonnet-5\"\n  }\n}\n")
            .Write("src/App/PrLock.cs", "class PrLock { }")
            .Write("docs/models.md", "\"DefaultModel\": \"claude-sonnet-5\"")
            .Write("tests/App.Tests/ModelTests.cs", "var model = \"claude-sonnet-5\"; // DefaultModel")
            .Write("src/App/nuget.config", "<configuration><DefaultModel>claude-sonnet-5</DefaultModel></configuration>")
            .CommitAsync();
        _facts = await RepoFacts.CollectAsync(_repo.Path, _repo.Git, null, CancellationToken.None);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose() => _repo.Dispose();

    [Fact]
    public async Task AWrongClaimWithAQuoteFromConfigIsAccepted()
    {
        var validation = await Validate(WrongModel("src/App/appsettings.json:3", "\"DefaultModel\": \"claude-sonnet-5\""));

        var issue = Assert.Single(validation.Accepted);
        Assert.Equal(["src/App/appsettings.json"], issue.Evidence);
        Assert.Equal(3, issue.Line);
    }

    [Theory]
    [InlineData("src/App/appsettings.json", "\"DefaultModel\": \"claude-opus-9\"", "isn't in any cited source or config file")]
    [InlineData("docs/models.md", "\"DefaultModel\": \"claude-sonnet-5\"", "isn't in any cited source or config file")]
    [InlineData("tests/App.Tests/ModelTests.cs", "var model = \"claude-sonnet-5\";", "isn't in any cited source or config file")]
    [InlineData("src/App/nuget.config", "<DefaultModel>claude-sonnet-5</DefaultModel>", "isn't in any cited source or config file")]
    [InlineData("src/App/appsettings.json", "sonnet-5", "shorter than")]
    [InlineData("src/App/appsettings.json", "{ \"Llm\": { \"DefaultModel\":", "doesn't contain anything the truth states")]
    public async Task AnEvidenceQuoteMustComeFromARealSourceFileAndShowTheTruth(string evidence, string quote, string reason)
    {
        var validation = await Validate(WrongModel(evidence, quote));

        Assert.Contains(reason, Assert.Single(validation.Rejected).Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWrongClaimNeedsTheTruthAndProof()
    {
        var noTruth = WrongModel("src/App/appsettings.json", "\"DefaultModel\": \"claude-sonnet-5\"") with { Truth = null };
        var noProof = WrongModel("src/App/appsettings.json", null);

        var validation = await Validate(noTruth, noProof);

        Assert.Equal(["a wrong claim needs the truth from the code", "a wrong claim needs an evidence quote or a missing term"], validation.Rejected.Select(r => r.Reason));
    }

    [Theory]
    [InlineData("map lock", true)]
    [InlineData("MapLock", true)]
    [InlineData("PR lock", false)]
    [InlineData("tenant context", false)]
    public async Task AMissingTermMustBeInTheReadmeAndNowhereInTheCode(string term, bool accepted)
    {
        var issue = new ReadmeIssue
        {
            Kind = IssueKind.WrongClaim,
            Quote = "PR locks and map locks keep reviews apart.",
            Truth = "There is no map lock; only PR locks.",
            MissingTerm = term,
            SuggestedFix = "Drop map locks.",
        };

        var validation = await Validate(issue);

        Assert.Equal(accepted, validation.Accepted.Count == 1);
    }

    [Fact]
    public async Task InADeepCheckOtherProblemsNeedProofButNotInAQuickOne()
    {
        var other = new ReadmeIssue { Kind = IssueKind.Other, Quote = "Lenses use Claude Sonnet 4.6 by default.", SuggestedFix = "?" };

        Assert.Single((await Validate(other)).Rejected);
        Assert.Single((await AssessmentValidator.ValidateAsync([other], new ValidationScope(_repo.Path, _repo.Git, _facts, []), CancellationToken.None)).Accepted);
    }

    [Fact]
    public async Task AQuoteFromAnotherPartIsRejected()
    {
        var validation = await Validate(WrongModel("src/App/appsettings.json", "\"DefaultModel\": \"claude-sonnet-5\""), firstLine: 5, lastLine: 7);

        Assert.Equal("the quote is in another part of the README than the one being checked", Assert.Single(validation.Rejected).Reason);
    }

    private Task<IssueValidation> Validate(ReadmeIssue issue, int firstLine = 1, int lastLine = 100) =>
        AssessmentValidator.ValidateAsync([issue], new ValidationScope(_repo.Path, _repo.Git, _facts, [], Deep: true, firstLine, lastLine), CancellationToken.None);

    private Task<IssueValidation> Validate(params ReadmeIssue[] issues) =>
        AssessmentValidator.ValidateAsync(issues, new ValidationScope(_repo.Path, _repo.Git, _facts, [], Deep: true), CancellationToken.None);

    private static ReadmeIssue WrongModel(string evidence, string? quote) => new()
    {
        Kind = IssueKind.WrongClaim,
        Quote = "Lenses use Claude Sonnet 4.6 by default.",
        Truth = "The default model is claude-sonnet-5.",
        Evidence = [evidence],
        EvidenceQuote = quote,
        SuggestedFix = "Say Claude Sonnet 5.",
    };
}
