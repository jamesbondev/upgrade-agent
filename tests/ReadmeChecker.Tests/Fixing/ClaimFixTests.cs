using ReadmeChecker.Agent;
using ReadmeChecker.Fixing;
using ReadmeChecker.Publishing;

namespace ReadmeChecker.Tests.Fixing;

public class ClaimFixTests
{
    private static readonly ReadmeIssue Model = new()
    {
        Kind = IssueKind.WrongClaim,
        Quote = "Lenses use Claude Sonnet 4.6.",
        Truth = "The default model is claude-sonnet-5.",
        EvidenceQuote = "\"DefaultModel\": \"claude-sonnet-5\"",
        Evidence = ["src/App/appsettings.json"],
        SuggestedFix = "Say Claude Sonnet 5.",
    };

    private static readonly ReadmeIssue MapLock = new()
    {
        Kind = IssueKind.WrongClaim,
        Quote = "PR locks and map locks.",
        Truth = "Only PR locks exist.",
        MissingTerm = "map lock",
        SuggestedFix = "Drop map locks.",
    };

    [Fact]
    public void AFixThatWritesWhatTheCodeSaysPasses()
    {
        var problems = new List<string>();

        var unchanged = ReadmeVerifier.CheckClaims([Model, MapLock], "Lenses use `claude-sonnet-5`.\n\nPR locks only.", problems);

        Assert.Empty(problems);
        Assert.Empty(unchanged);
    }

    [Fact]
    public void AFixThatChangesAClaimToSomethingElseIsRejected()
    {
        var problems = new List<string>();

        ReadmeVerifier.CheckClaims([Model], "Lenses use Claude Opus 9.", problems);

        Assert.StartsWith("it changed \"Lenses use Claude Sonnet 4.6.\" but the new text doesn't say what the code says", Assert.Single(problems), StringComparison.Ordinal);
    }

    [Fact]
    public void ATermThatNoLongerExistsMustBeGone()
    {
        var problems = new List<string>();

        ReadmeVerifier.CheckClaims([MapLock], "PR locks and the map-lock table.", problems);

        Assert.Equal("it still mentions 'map lock', which no longer exists in the code", Assert.Single(problems));
    }

    [Fact]
    public void AClaimTheFixLeftAloneIsListedNotRejectedUnlessNothingChanged()
    {
        var problems = new List<string>();
        var other = new ReadmeIssue { Kind = IssueKind.BrokenReference, Quote = "[x](gone.md)", SuggestedFix = "Drop it." };

        var unchanged = ReadmeVerifier.CheckClaims([Model, other], "Lenses use Claude Sonnet 4.6.", problems);
        var allUnchanged = new List<string>();
        ReadmeVerifier.CheckClaims([Model], "Lenses use Claude Sonnet 4.6. More text.", allUnchanged);

        Assert.Equal([Model], unchanged);
        Assert.Empty(problems);
        Assert.Equal(["none of the wrong claims was changed"], allUnchanged);
    }

    [Fact]
    public void ThePullRequestShowsTheTruthAndWhatWasLeftAlone()
    {
        var description = PullRequestText.Description("README.md", [Model, MapLock], [], null, [MapLock]);

        Assert.Contains("The code says: The default model is claude-sonnet-5. (src/App/appsettings.json)", description, StringComparison.Ordinal);
        Assert.Contains("## Left unchanged", description, StringComparison.Ordinal);
        Assert.Contains("\"PR locks and map locks.\": the checker says Only PR locks exist.", description, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFixPromptPresentsTheTruthAsSomethingToVerify()
    {
        var prompt = FixPrompts.Problems([Model, MapLock], []);

        Assert.Contains("The checker says the code has: The default model is claude-sonnet-5. (verify it before you write it)", prompt, StringComparison.Ordinal);
        Assert.Contains("The checker found no \"map lock\" anywhere in the code", prompt, StringComparison.Ordinal);
    }
}
