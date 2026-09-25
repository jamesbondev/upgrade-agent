using AgentHarness.Policies;
using AgentHarness.Testing;
using ReadmeChecker.Agent;
using ReadmeChecker.Config;
using ReadmeChecker.Detection;
using ReadmeChecker.Fixing;
using ReadmeChecker.Publishing;
using ReadmeChecker.Run;
using ReadmeChecker.Tests.Agent;
using ReadmeChecker.Tests.TestSupport;
using RepoKit;
using RepoKit.AzureDevOps;

namespace ReadmeChecker.Tests.Run;

public sealed class FixOrchestratorTests : IAsyncLifetime, IDisposable
{
    private const string StaleReadme = """
        # Demo

        See the [setup guide](docs/setup.md).

        Run `dotnet run --project src/Old`.

        ## Contributing

        Open a pull request.
        """;

    private static readonly string FixedReadme = StaleReadme
        .Replace("docs/setup.md", "docs/guide.md", StringComparison.Ordinal)
        .Replace("src/Old", "src/New", StringComparison.Ordinal);

    private readonly TempDirectory _out = new();
    private readonly TempDirectory _work = new();
    private readonly FakeHosts _hosts = new();
    private TempRepo _repo = null!;

    public async Task InitializeAsync()
    {
        _repo = await TempRepo.CreateAsync();
        await _repo.Write("README.md", StaleReadme).Write("docs/guide.md", "guide").Write("src/New/New.csproj", "<Project />").CommitAsync();
    }

    public Task DisposeAsync()
    {
        _repo.Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _out.Dispose();
        _work.Dispose();
    }

    [Fact]
    public async Task AFixThatPassesTheChecksIsPushedAsADraftPullRequest()
    {
        var backend = Scripted(fix: t => t.Edit("README.md", FixedReadme).Reply("Fixed the command and the link."));

        var repo = await RunAsync(backend, ApprovalPrompter.From((_, _) => true));

        Assert.Equal(FixStatus.Opened, repo.Status);
        Assert.Equal("https://example.invalid/pullrequest/1", repo.PullRequestUrl);
        var created = Assert.Single(_hosts.Host.Created);
        Assert.Equal((repo.Branch, "main", PullRequestText.Title), (created.Branch, created.Target, created.Title));
        Assert.StartsWith("agent/readme-refresh-", repo.Branch, StringComparison.Ordinal);
        Assert.Contains("Fixed the command and the link.", created.Description, StringComparison.Ordinal);
        Assert.Equal(FixedReadme, await _repo.Git.ShowFileAsync(_repo.Path, repo.Branch!, "README.md"));
        Assert.StartsWith("docs: update README.md", await _repo.RunAsync("log", "-1", "--format=%B", repo.Branch!), StringComparison.Ordinal);
        Assert.True(File.Exists(repo.PatchPath));
    }

    [Fact]
    public async Task AnOpenPullRequestMeansTheRepoIsSkipped()
    {
        _hosts.Host.Existing.Add(PullRequest("active", DateTimeOffset.UtcNow.AddDays(-90)));
        var backend = Scripted(fix: t => t.Reply("never"));

        var repo = await RunAsync(backend, ApprovalPrompter.From((_, _) => true));

        Assert.Equal(FixStatus.Skipped, repo.Status);
        Assert.StartsWith("a pull request is already open", repo.Reason, StringComparison.Ordinal);
        Assert.Equal(2, backend.Messages.Count);
    }

    [Theory]
    [InlineData(10, "Skipped")]
    [InlineData(60, "Opened")]
    public async Task ARecentPullRequestHoldsOffAnotherOne(int daysAgo, string expected)
    {
        _hosts.Host.Existing.Add(PullRequest("completed", DateTimeOffset.UtcNow.AddDays(-daysAgo)));
        var backend = Scripted(fix: t => t.Edit("README.md", FixedReadme).Reply("done"));

        var repo = await RunAsync(backend, ApprovalPrompter.From((_, _) => true));

        Assert.Equal(Enum.Parse<FixStatus>(expected), repo.Status);
    }

    [Fact]
    public async Task ADryRunPushesNothing()
    {
        var backend = Scripted(fix: t => t.Edit("README.md", FixedReadme).Reply("done"));

        var repo = await RunAsync(backend, ApprovalPrompter.From((_, _) => true), dryRun: true);

        Assert.Equal((FixStatus.Ready, "dry run: nothing was pushed"), (repo.Status, repo.Reason));
        Assert.Empty(_hosts.Host.Created);
        Assert.False(await BranchExists(repo.Branch!));
        Assert.Contains("+Run `dotnet run --project src/New`.", await File.ReadAllTextAsync(repo.PatchPath!), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeclinedFixIsNotPushed()
    {
        var backend = Scripted(fix: t => t.Edit("README.md", FixedReadme).Reply("done"));

        var repo = await RunAsync(backend, ApprovalPrompter.DeclineAll);

        Assert.Equal(FixStatus.Declined, repo.Status);
        Assert.False(await BranchExists(repo.Branch!));
    }

    [Fact]
    public async Task AFixThatFailsTheChecksIsRejected()
    {
        var backend = Scripted(fix: t => t.Edit("README.md", StaleReadme + "\nAlso see `src/Invented/Thing.cs`.\n").Reply("done"));

        var repo = await RunAsync(backend, ApprovalPrompter.From((_, _) => true));

        Assert.Equal(FixStatus.Rejected, repo.Status);
        Assert.Contains(repo.Problems, p => p.Contains("src/Invented/Thing.cs", StringComparison.Ordinal));
        Assert.Contains(repo.Problems, p => p.StartsWith("broken links are still there", StringComparison.Ordinal));
        Assert.Empty(_hosts.Host.Created);
    }

    [Fact]
    public async Task ALocalRepoWithoutAPullRequestHostGetsAPatch()
    {
        _hosts.None = true;
        var backend = Scripted(fix: t => t.Edit("README.md", FixedReadme).Reply("done"));

        var repo = await RunAsync(backend, ApprovalPrompter.From((_, _) => true));

        Assert.Equal((FixStatus.Ready, "local repo: the fix is saved as a patch, not pushed"), (repo.Status, repo.Reason));
    }

    [Fact]
    public async Task AReadmeThatIsCurrentIsLeftAlone()
    {
        using var fresh = await TempRepo.CreateAsync();
        await fresh.Write("README.md", "# Fresh\n\nRun `dotnet run --project src/New`.\n").Write("src/New/New.csproj", "<Project />").CommitAsync();
        var backend = new ScriptedBackend();

        var report = await Orchestrator(backend, new TargetList(new RepoTarget("fresh", null, fresh.Path, null)))
            .RunAsync(new FixArguments([], false, ApprovalPrompter.From((_, _) => true)), CancellationToken.None);

        var repo = Assert.Single(report.Repos);
        Assert.Equal(FixStatus.NothingToFix, repo.Status);
        Assert.Empty(backend.Messages);
        Assert.True(File.Exists(Path.Combine(report.OutputDirectory, "fix-report.md")));
    }

    [Fact]
    public void ABrokenLinkAnIssueAlreadyQuotesIsListedOnce()
    {
        var covered = new Signal(SignalKind.BrokenLink, 3, "docs/setup.md", "gone", "docs/setup.md");
        var other = new Signal(SignalKind.BrokenLink, 9, "docs/old.md", "gone", "docs/old.md");
        var maybe = new Signal(SignalKind.MissingPath, 5, "src/x.cs", "gone", "src/x.cs");
        var issue = new ReadmeIssue { Kind = IssueKind.BrokenReference, Quote = "See the [setup guide](docs/setup.md).", SuggestedFix = "Link the guide." };

        Assert.Equal([other], FixOrchestrator.UncoveredBrokenLinks([covered, other, maybe], [issue]));
    }

    private static ScriptedBackend Scripted(Action<ScriptedTurn> fix) => new ScriptedBackend()
        .Turn(t => t.Read("README.md").Reply("checked"))
        .Turn(t => t.ReplyJson(new ReadmeAssessment
        {
            Verdict = AssessedVerdict.Stale,
            Summary = "The run command is stale.",
            Issues = [new ReadmeIssue { Kind = IssueKind.WrongCommand, Quote = "dotnet run --project src/Old", Evidence = ["src/New/New.csproj"], SuggestedFix = "Use src/New." }],
        }))
        .Turn(fix);

    private async Task<RepoFixReport> RunAsync(ScriptedBackend backend, IApprovalPrompter prompter, bool dryRun = false)
    {
        var report = await Orchestrator(backend, new TargetList(new RepoTarget("demo", null, _repo.Path, null)))
            .RunAsync(new FixArguments([], dryRun, prompter), CancellationToken.None);
        return Assert.Single(report.Repos);
    }

    private FixOrchestrator Orchestrator(ScriptedBackend backend, TargetList targets)
    {
        var options = new ReadmeCheckerOptions();
        var config = new ResolvedConfig(options, targets.Targets, _out.Path, _work.Path);
        var factory = new ReadmeAssessorTests.ScriptedFactory(backend);
        var credentials = new AzureDevOpsCredentialProvider(new AzureDevOpsAuthOptions { UseAzureIdentity = false }, _ => null);
        var inspector = new RepoInspector(config, new GitCli(new ProcessRunner()), credentials, new ReadmeAssessor(factory, options.Agent, TimeProvider.System), TimeProvider.System);
        return new FixOrchestrator(config, inspector, new ReadmeFixer(factory, options.Agent, TimeProvider.System), _hosts, new NoProgress(), TimeProvider.System);
    }

    private async Task<bool> BranchExists(string branch) =>
        (await _repo.Git.TryRunAsync(_repo.Path, ["rev-parse", "--verify", "--quiet", $"refs/heads/{branch}"])).Succeeded;

    private static AzureDevOpsPullRequest PullRequest(string status, DateTimeOffset created) =>
        new(9, "Update the README", status, "agent/readme-refresh-20260101-000000", "main", true, created, "https://example.invalid/pullrequest/9");

    private sealed record TargetList(params RepoTarget[] Targets);

    private sealed class FakeHosts : IPullRequestHosts
    {
        public FakeHost Host { get; } = new();

        public bool None { get; set; }

        public IPullRequestHost? For(RepoTarget target, AzureDevOpsCredential? credential) => None ? null : Host;
    }

    private sealed class FakeHost : IPullRequestHost
    {
        public List<AzureDevOpsPullRequest> Existing { get; } = [];

        public List<(string Branch, string Target, string Title, string Description)> Created { get; } = [];

        public Task<IReadOnlyList<AzureDevOpsPullRequest>> ListAsync(string branchPrefix, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AzureDevOpsPullRequest>>(Existing);

        public Task<AzureDevOpsPullRequest> CreateDraftAsync(string branch, string targetBranch, string title, string description, CancellationToken cancellationToken)
        {
            Created.Add((branch, targetBranch, title, description));
            return Task.FromResult(new AzureDevOpsPullRequest(Created.Count, title, "active", branch, targetBranch, true, DateTimeOffset.UtcNow, $"https://example.invalid/pullrequest/{Created.Count}"));
        }
    }

    private sealed class NoProgress : IFixProgress
    {
        public void RepoStarted(RepoTarget target, int index, int count)
        {
        }

        public void RepoFinished(RepoFixReport report)
        {
        }

        public void RunFinished(FixRunReport report, string reportPath)
        {
        }
    }
}
