using AgentHarness.Testing;
using ReadmeChecker.Agent;
using ReadmeChecker.Config;
using ReadmeChecker.Detection;
using ReadmeChecker.Run;
using ReadmeChecker.Tests.Agent;
using ReadmeChecker.Tests.TestSupport;
using RepoKit;
using RepoKit.AzureDevOps;

namespace ReadmeChecker.Tests.Run;

public sealed class CheckOrchestratorTests : IAsyncLifetime, IDisposable
{
    private readonly TempDirectory _out = new();
    private readonly TempDirectory _work = new();
    private TempRepo _stale = null!;
    private TempRepo _fresh = null!;
    private TempRepo _noReadme = null!;

    public async Task InitializeAsync()
    {
        _stale = await TempRepo.CreateAsync();
        await _stale.Write("README.md", "# Stale\n\nSee [setup](docs/setup.md). Run `dotnet run --project src/Old`.\n").Write("src/New/New.csproj", "<Project />").CommitAsync();

        _fresh = await TempRepo.CreateAsync();
        await _fresh.Write("README.md", "# Fresh\n\nRun `dotnet run --project src/App`.\n").Write("src/App/App.csproj", "<Project />").CommitAsync();

        _noReadme = await TempRepo.CreateAsync();
        await _noReadme.Write("src/a.cs", "class A;").CommitAsync();
    }

    public Task DisposeAsync()
    {
        _stale.Dispose();
        _fresh.Dispose();
        _noReadme.Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _out.Dispose();
        _work.Dispose();
    }

    [Fact]
    public async Task EachRepoGetsAVerdictAndOneFailureDoesntStopTheRun()
    {
        var backend = new ScriptedBackend()
            .Turn(t => t.Read("README.md").Reply("checked"))
            .Turn(t => t.ReplyJson(new ReadmeAssessment
            {
                Verdict = AssessedVerdict.Stale,
                Summary = "Two stale references.",
                Issues = [new ReadmeIssue { Kind = IssueKind.WrongCommand, Quote = "dotnet run --project src/Old", Evidence = ["src/New/New.csproj"], SuggestedFix = "Use src/New." }],
            }));
        var progress = new ListProgress();

        var report = await Orchestrator(backend, progress, Local("stale", _stale), Local("fresh", _fresh), Local("bare", _noReadme), Local("gone", _work.Combine("nowhere")))
            .RunAsync(new CheckArguments([], AgentProvider.Copilot), CancellationToken.None);

        var verdicts = report.Repos.ToDictionary(r => r.Name, r => (r.Verdict, r.Deterministic));
        Assert.Equal((RepoVerdict.Stale, false), verdicts["stale"]);
        Assert.Equal((RepoVerdict.Current, true), verdicts["fresh"]);
        Assert.Equal((RepoVerdict.Missing, true), verdicts["bare"]);
        Assert.Equal(RepoVerdict.Error, verdicts["gone"].Verdict);
        Assert.Contains("does not exist", report.Repos.Single(r => r.Name == "gone").Note, StringComparison.Ordinal);

        var stale = report.Repos.Single(r => r.Name == "stale");
        Assert.Single(stale.Issues);
        Assert.Contains(stale.Signals, s => s.Kind == SignalKind.BrokenLink && s.Target == "docs/setup.md");
        Assert.Equal(2, backend.Messages.Count);

        Assert.True(File.Exists(Path.Combine(report.OutputDirectory, "report.md")));
        Assert.True(File.Exists(Path.Combine(report.OutputDirectory, "report.json")));
        Assert.True(File.Exists(Path.Combine(report.OutputDirectory, "stale", "assessment.json")));
        Assert.True(File.Exists(Path.Combine(report.OutputDirectory, "stale", "agent.log")));
        Assert.Equal(["stale", "fresh", "bare", "gone"], progress.Finished.Select(r => r.Name));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_work.Path));
    }

    [Fact]
    public async Task WithoutTheAgentBrokenLinksStillMakeItStale()
    {
        var report = await Orchestrator(new ScriptedBackend(), new ListProgress(), Local("stale", _stale))
            .RunAsync(new CheckArguments([], AgentProvider.None), CancellationToken.None);

        var repo = Assert.Single(report.Repos);
        Assert.Equal((RepoVerdict.Stale, true), (repo.Verdict, repo.Deterministic));
        Assert.Equal("broken links found; agent turned off", repo.Note);
    }

    [Fact]
    public async Task OnlyNarrowsTheRunAndUnknownNamesAreAConfigurationError()
    {
        var orchestrator = Orchestrator(new ScriptedBackend(), new ListProgress(), Local("stale", _stale), Local("fresh", _fresh));

        var report = await orchestrator.RunAsync(new CheckArguments(["FRESH"], AgentProvider.None), CancellationToken.None);
        var error = await Assert.ThrowsAsync<ConfigurationException>(() => orchestrator.RunAsync(new CheckArguments(["nope"], AgentProvider.None), CancellationToken.None));

        Assert.Equal(["fresh"], report.Repos.Select(r => r.Name));
        Assert.Contains("nope", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CopilotNotBeingReadyStopsTheWholeRun()
    {
        var factory = new ReadmeAssessorTests.ScriptedFactory(new ScriptedBackend()) { NotReady = "not signed in" };
        var orchestrator = Orchestrator(factory, new ListProgress(), Local("stale", _stale));

        await Assert.ThrowsAsync<AgentUnavailableException>(() => orchestrator.RunAsync(new CheckArguments([], AgentProvider.Copilot), CancellationToken.None));
    }

    [Theory]
    [InlineData(null, "Stale", 1, false, "Stale")]
    [InlineData(null, "Stale", 0, false, "Unsure")]
    [InlineData(null, "Stale", 0, true, "Stale")]
    [InlineData(null, "Current", 0, false, "Current")]
    [InlineData(null, "Current", 0, true, "Stale")]
    [InlineData(null, "Unsure", 0, false, "Unsure")]
    [InlineData("the agent failed", null, 0, false, "Unsure")]
    [InlineData("the agent failed", null, 0, true, "Stale")]
    public void TheVerdictCombinesTheAgentWithCertainSignals(string? failure, string? verdict, int issues, bool brokenLink, string expected)
    {
        var scan = new SignalScan(brokenLink ? [new Signal(SignalKind.BrokenLink, 1, "x.md", "gone", "x.md")] : [], 0);
        var accepted = Enumerable.Range(0, issues).Select(_ => new ReadmeIssue { Kind = IssueKind.Other, Quote = "q", SuggestedFix = "f" }).ToList();
        var outcome = new AssessmentOutcome(verdict is null ? null : Enum.Parse<AssessedVerdict>(verdict), accepted, [], null, null, failure);

        var report = RepoInspector.Combine(RepoReport.For(new RepoTarget("r", null, "/r", null), RepoVerdict.Unassessed), scan, outcome);

        Assert.Equal(Enum.Parse<RepoVerdict>(expected), report.Verdict);
    }

    private CheckOrchestrator Orchestrator(ScriptedBackend backend, ListProgress progress, params RepoTarget[] repos) =>
        Orchestrator(new ReadmeAssessorTests.ScriptedFactory(backend), progress, repos);

    private CheckOrchestrator Orchestrator(IAgentBackendFactory factory, ListProgress progress, params RepoTarget[] repos)
    {
        var options = new ReadmeCheckerOptions();
        var config = new ResolvedConfig(options, repos, _out.Path, _work.Path);
        var credentials = new AzureDevOpsCredentialProvider(new AzureDevOpsAuthOptions { UseAzureIdentity = false }, _ => null);
        var assessor = new ReadmeAssessor(factory, options.Agent, TimeProvider.System);
        var inspector = new RepoInspector(config, new GitCli(new ProcessRunner()), credentials, assessor, TimeProvider.System);
        return new CheckOrchestrator(inspector, progress, TimeProvider.System);
    }

    private static RepoTarget Local(string name, TempRepo repo) => new(name, null, repo.Path, null);

    private static RepoTarget Local(string name, string path) => new(name, null, path, null);

    private sealed class ListProgress : ICheckProgress
    {
        public List<RepoReport> Finished { get; } = [];

        public void RepoStarted(RepoTarget target, int index, int count)
        {
        }

        public void RepoFinished(RepoReport report) => Finished.Add(report);

        public void RunFinished(CheckReport report, string reportPath)
        {
        }
    }
}
