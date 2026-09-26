using AgentHarness;
using AgentHarness.Testing;
using ReadmeChecker.Agent;
using ReadmeChecker.Config;
using ReadmeChecker.Detection;
using ReadmeChecker.Tests.TestSupport;

namespace ReadmeChecker.Tests.Agent;

public sealed class DeepReadmeAssessorTests : IAsyncLifetime, IDisposable
{
    private readonly TempDirectory _out = new();
    private TempRepo _repo = null!;
    private RepoFacts _facts = null!;

    public async Task InitializeAsync()
    {
        List<string> lines = ["# Demo", "", "Lenses use Claude Sonnet 4.6 by default."];
        var readme = string.Join('\n',
            lines
                .Concat(Enumerable.Range(0, 80).Select(i => $"filler {i}"))
                .Concat(["## Concurrency", "", "PR locks and map locks keep reviews apart."])
                .Concat(Enumerable.Range(0, 80).Select(i => $"more {i}"))
                .Concat(["## Hosts", "", "There are two hosts."]));
        _repo = await TempRepo.CreateAsync();
        await _repo.Write("README.md", readme).Write("src/App/appsettings.json", "{ \"DefaultModel\": \"claude-sonnet-5\" }").CommitAsync();
        _facts = await RepoFacts.CollectAsync(_repo.Path, _repo.Git, null, CancellationToken.None);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _repo.Dispose();
        _out.Dispose();
    }

    [Fact]
    public async Task EachPartIsCheckedInItsOwnSessionAndOnlyProvenProblemsSurvive()
    {
        var backend = new ScriptedBackend()
            .Turn(t => t.Read("src/App/appsettings.json").Step(Credits(1.5)).Reply("checked part 1"))
            .Turn(t => t.ReplyJson(Check(3, WrongModel())))
            .Turn(t => t.Step(Credits(1)).Reply("checked part 2"))
            .Turn(t => t.ReplyJson(Check(2, MapLock(), Invented())))
            .Turn(t => t.Reply("checked part 3"))
            .Turn(t => t.ReplyJson(Check(1)));

        var outcome = await Assess(backend);

        Assert.Equal(AssessedVerdict.Stale, outcome.Verdict);
        Assert.Equal(["Lenses use Claude Sonnet 4.6 by default.", "PR locks and map locks keep reviews apart."], outcome.Issues.Select(i => i.Quote));
        Assert.Equal("There are three hosts in the code.", Assert.Single(outcome.Rejected).Issue.Truth);
        Assert.Equal(6, outcome.Coverage!.ClaimsChecked);
        Assert.Equal([ChunkStatus.Checked, ChunkStatus.Checked, ChunkStatus.Checked], outcome.Coverage.Parts.Select(p => p.Status));
        Assert.Equal(2.5, outcome.Stats!.AiCredits);
        Assert.Contains("Your part: 3 of 3", backend.Messages[4], StringComparison.Ordinal);
    }

    [Fact]
    public async Task APartThatHitsItsLimitStillAnswersAndCountsAsPartlyChecked()
    {
        var backend = new ScriptedBackend()
            .Turn(t => t.Shell("ls").Shell("ls src").Shell("ls src/App").Reply("never"))
            .Turn(t => t.ReplyJson(Check(1, WrongModel())))
            .Turn(t => t.Reply("ok")).Turn(t => t.ReplyJson(Check(1)))
            .Turn(t => t.Reply("ok")).Turn(t => t.ReplyJson(Check(1)));

        var outcome = await Assess(backend, new AgentOptions { MaxToolCalls = 2 });

        Assert.Single(outcome.Issues);
        Assert.Equal(ChunkStatus.PartlyChecked, outcome.Coverage!.Parts[0].Status);
        Assert.Equal(AssessedVerdict.Stale, outcome.Verdict);
    }

    [Fact]
    public async Task AFailedPartIsNotCheckedAndMakesACleanResultUnsure()
    {
        var backend = new ScriptedBackend()
            .Turn(t => t.Reply("ok")).Turn(t => t.Reply("not json"))
            .Turn(t => t.Reply("ok")).Turn(t => t.ReplyJson(Check(1)))
            .Turn(t => t.Reply("ok")).Turn(t => t.ReplyJson(Check(1)));

        var outcome = await Assess(backend);

        Assert.Equal(AssessedVerdict.Unsure, outcome.Verdict);
        Assert.Equal(ChunkStatus.NotChecked, outcome.Coverage!.Parts[0].Status);
        Assert.Null(outcome.Failure);
    }

    [Fact]
    public async Task ASpentBudgetSkipsTheRemainingParts()
    {
        var backend = new ScriptedBackend()
            .Turn(t => t.Step(Credits(3)).Reply("ok")).Turn(t => t.ReplyJson(Check(1)));

        var outcome = await Assess(backend, budget: 2);

        Assert.Equal([ChunkStatus.Checked, ChunkStatus.Skipped, ChunkStatus.Skipped], outcome.Coverage!.Parts.Select(p => p.Status));
        Assert.Equal(AssessedVerdict.Unsure, outcome.Verdict);
    }

    [Fact]
    public async Task AUsedUpQuotaStopsTheRunInsteadOfFailingEachPart()
    {
        var backend = new ScriptedBackend()
            .Turn(t => t.Step((_, _) => throw new InvalidOperationException("Session error: You have exceeded your monthly quota")));

        var error = await Assert.ThrowsAsync<AgentUnavailableException>(() => Assess(backend));

        Assert.Contains("quota", error.Message, StringComparison.Ordinal);
    }

    private Task<AssessmentOutcome> Assess(ScriptedBackend backend, AgentOptions? options = null, double? budget = null) =>
        new DeepReadmeAssessor(new ReadmeAssessorTests.ScriptedFactory(backend), options ?? new AgentOptions(), _repo.Git, TimeProvider.System)
            .AssessAsync("demo", _repo.Path, _facts, SignalScan.Empty, _out.Combine("agent.log"), budget, CancellationToken.None);

    private static Func<ScriptedBackend.ScriptedContext, CancellationToken, Task> Credits(double credits) => (c, _) =>
    {
        c.Raise(new ModelUsage("scripted-model", 100, 10, credits));
        return Task.CompletedTask;
    };

    private static ChunkCheck Check(int claims, params ReadmeIssue[] problems) =>
        new() { ClaimsChecked = claims, Problems = problems, Summary = "done" };

    private static ReadmeIssue WrongModel() => new()
    {
        Kind = IssueKind.WrongClaim,
        Quote = "Lenses use Claude Sonnet 4.6 by default.",
        Truth = "The default model is claude-sonnet-5.",
        Evidence = ["src/App/appsettings.json"],
        EvidenceQuote = "\"DefaultModel\": \"claude-sonnet-5\"",
        SuggestedFix = "Say Claude Sonnet 5.",
    };

    private static ReadmeIssue MapLock() => new()
    {
        Kind = IssueKind.WrongClaim,
        Quote = "PR locks and map locks keep reviews apart.",
        Truth = "Only PR locks exist.",
        MissingTerm = "map lock",
        SuggestedFix = "Drop map locks.",
    };

    private static ReadmeIssue Invented() => new()
    {
        Kind = IssueKind.WrongClaim,
        Quote = "There are two hosts.",
        Truth = "There are three hosts in the code.",
        Evidence = ["src/App/appsettings.json"],
        EvidenceQuote = "var hosts = new[] { Api, Worker, Processor };",
        SuggestedFix = "Say three.",
    };
}
