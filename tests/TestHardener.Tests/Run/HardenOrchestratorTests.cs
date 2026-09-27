using AgentHarness;
using AgentHarness.Policies;
using AgentHarness.Testing;
using Microsoft.Extensions.Time.Testing;
using RepoKit;
using RepoKit.AzureDevOps;
using TestHardener.Config;
using TestHardener.Hardening;
using TestHardener.Publishing;
using TestHardener.Run;
using TestHardener.Stryker;
using TestHardener.Tests.TestSupport;

namespace TestHardener.Tests.Run;

public sealed class HardenOrchestratorTests : IDisposable
{
    private const string TestFile = "tests/Demo.Tests/CalculatorTests.cs";
    private const string Written = "namespace Demo.Tests;\n\npublic class CalculatorTests\n{\n    [Fact]\n    public void Add_Bigger_Sums() => Assert.Equal(3, 3);\n}\n";

    private readonly TempDirectory _output = new("th-hout-");
    private readonly TempDirectory _work = new("th-hwork-");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero));
    private TempRepo? _origin;

    [Fact]
    public async Task RunAsync_Approved_CommitsOnlyTheTestFilePushesAndOpensADraft()
    {
        var origin = await OriginAsync();
        var host = new FakeHost();

        var report = await Orchestrator(host, new FakeHardener(Verified)).RunAsync(Arguments(approve: true), CancellationToken.None);

        var repo = Assert.Single(report.Repos);
        Assert.Equal(HardenStatus.Opened, repo.Status);
        Assert.Equal("https://example.invalid/pr/1", repo.PullRequestUrl);
        var created = Assert.Single(host.Created);
        Assert.Equal("agent/test-hardening-20260927-120000", created.Branch);
        Assert.Equal("main", created.Target);
        Assert.Contains("`Add_Bigger_Sums`", created.Description, StringComparison.Ordinal);
        Assert.Contains("Read each assertion", created.Description, StringComparison.Ordinal);

        var files = await origin.RunAsync("diff", "--name-only", "main", created.Branch);
        Assert.Equal(TestFile, files.Trim());
        var message = await origin.RunAsync("log", "-1", "--format=%B", created.Branch);
        Assert.StartsWith("test(demo): cover surviving mutants", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_OpenPullRequest_SkipsBeforeSurveying()
    {
        await OriginAsync();
        var host = new FakeHost { Existing = [PullRequest("active", _time.GetUtcNow().AddDays(-30))] };
        var stryker = new FakeStryker(_ => throw new InvalidOperationException("must not survey"));
        var hardener = new FakeHardener(Verified);

        var report = await Orchestrator(host, hardener, stryker).RunAsync(Arguments(approve: true), CancellationToken.None);

        Assert.Equal(HardenStatus.Skipped, report.Repos.Single().Status);
        Assert.Contains("already open", report.Repos.Single().Note, StringComparison.Ordinal);
        Assert.Equal(0, hardener.Calls);
    }

    [Fact]
    public async Task RunAsync_RecentPullRequest_IsSkippedWithinTheCooldown()
    {
        await OriginAsync();
        var host = new FakeHost { Existing = [PullRequest("completed", _time.GetUtcNow().AddDays(-2))] };

        var report = await Orchestrator(host, new FakeHardener(Verified)).RunAsync(Arguments(approve: true), CancellationToken.None);

        Assert.Equal(HardenStatus.Skipped, report.Repos.Single().Status);
    }

    [Fact]
    public async Task RunAsync_Declined_PushesNothing()
    {
        var origin = await OriginAsync();
        var host = new FakeHost();

        var report = await Orchestrator(host, new FakeHardener(Verified)).RunAsync(Arguments(approve: false), CancellationToken.None);

        var repo = Assert.Single(report.Repos);
        Assert.Equal(HardenStatus.Declined, repo.Status);
        Assert.True(File.Exists(repo.PatchPath));
        Assert.Empty(host.Created);
        Assert.Equal("main", (await origin.RunAsync("branch", "--format=%(refname:short)")).Trim());
    }

    [Fact]
    public async Task RunAsync_DryRun_SavesThePatchOnly()
    {
        await OriginAsync();
        var host = new FakeHost();

        var report = await Orchestrator(host, new FakeHardener(Verified)).RunAsync(Arguments(approve: true, dryRun: true), CancellationToken.None);

        var repo = Assert.Single(report.Repos);
        Assert.Equal(HardenStatus.Ready, repo.Status);
        Assert.Contains("+    public void Add_Bigger_Sums()", await File.ReadAllTextAsync(repo.PatchPath!), StringComparison.Ordinal);
        Assert.Empty(host.Created);
    }

    [Fact]
    public async Task RunAsync_NoGroupVerified_IsRejectedWithoutPublishing()
    {
        await OriginAsync();
        var host = new FakeHost();

        var report = await Orchestrator(host, new FakeHardener(Rejected)).RunAsync(Arguments(approve: true), CancellationToken.None);

        Assert.Equal(HardenStatus.Rejected, report.Repos.Single().Status);
        Assert.Empty(host.Created);
    }

    [Fact]
    public async Task RunAsync_FinalCheckFails_IsRejected()
    {
        await OriginAsync();
        var host = new FakeHost();

        var report = await Orchestrator(host, new FakeHardener(Verified), finalTestsPass: false).RunAsync(Arguments(approve: true), CancellationToken.None);

        var repo = Assert.Single(report.Repos);
        Assert.Equal(HardenStatus.Rejected, repo.Status);
        Assert.Contains("fails with the new tests", repo.Note, StringComparison.Ordinal);
        Assert.Empty(host.Created);
    }

    [Fact]
    public async Task RunAsync_ListsPushedBranchesThatHaveNoPullRequest()
    {
        var origin = await OriginAsync();
        await origin.RunAsync("branch", "agent/test-hardening-20260101-000000");
        await origin.RunAsync("branch", "agent/test-hardening-20260102-000000");
        var host = new FakeHost { Existing = [PullRequest("abandoned", _time.GetUtcNow().AddDays(-60), "agent/test-hardening-20260102-000000")] };

        var report = await Orchestrator(host, new FakeHardener(Verified)).RunAsync(Arguments(approve: false), CancellationToken.None);

        Assert.Equal(["agent/test-hardening-20260101-000000"], report.Repos.Single().LeftoverBranches);
    }

    [Fact]
    public async Task RunAsync_NoHost_KeepsTheTestsAsAPatch()
    {
        await OriginAsync();

        var report = await Orchestrator(null, new FakeHardener(Verified)).RunAsync(Arguments(approve: true), CancellationToken.None);

        Assert.Equal(HardenStatus.Ready, report.Repos.Single().Status);
        Assert.Contains("local repo", report.Repos.Single().Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_OneGroupThrows_TheOtherGroupsStillCount()
    {
        await OriginAsync();
        var calls = 0;
        var hardener = new FakeHardener(job => ++calls == 1 ? throw new InvalidOperationException("boom") : Verified(job));

        var report = await Orchestrator(new FakeHost(), hardener, new FakeStryker(TwoGroupReport)).RunAsync(Arguments(approve: false), CancellationToken.None);

        var repo = Assert.Single(report.Repos);
        Assert.Equal([GroupOutcome.Failed, GroupOutcome.Verified], repo.Groups.Select(g => g.Outcome));
        Assert.Equal("boom", repo.Groups[0].Reason);
        Assert.Equal(HardenStatus.Declined, repo.Status);
    }

    [Fact]
    public async Task RunAsync_QuotaRunsOut_KeepsVerifiedWorkWritesTheReportAndStops()
    {
        await OriginAsync();
        var calls = 0;
        var hardener = new FakeHardener(job => ++calls == 1 ? Verified(job) : throw new AgentUnavailableException("quota used up"));

        var error = await Assert.ThrowsAsync<AgentUnavailableException>(() =>
            Orchestrator(new FakeHost(), hardener, new FakeStryker(TwoGroupReport)).RunAsync(Arguments(approve: true, dryRun: true), CancellationToken.None));

        Assert.Equal("quota used up", error.Message);
        var run = Directory.GetDirectories(_output.Path, "run-*").Single();
        Assert.True(File.Exists(Path.Combine(run, "harden-report.md")));
        Assert.True(File.Exists(Path.Combine(run, "demo", "hardening.patch")));
        Assert.Contains("Copilot stopped the run after 1 groups", await File.ReadAllTextAsync(Path.Combine(run, "harden-report.md")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_QuotaRunsOutBeforeAnyGroup_IsAFailureNotARejection()
    {
        await OriginAsync();
        var hardener = new FakeHardener(_ => throw new AgentUnavailableException("quota used up"));

        await Assert.ThrowsAsync<AgentUnavailableException>(() =>
            Orchestrator(new FakeHost(), hardener).RunAsync(Arguments(approve: true, dryRun: true), CancellationToken.None));

        var report = await File.ReadAllTextAsync(Path.Combine(Directory.GetDirectories(_output.Path, "run-*").Single(), "harden-report.md"));
        Assert.Contains("## demo: Failed", report, StringComparison.Ordinal);
        Assert.Contains("Copilot stopped the run before any group passed: quota used up", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_CreditCap_SkipsTheRemainingGroups()
    {
        await OriginAsync();
        var hardener = new FakeHardener(async job => await Verified(job) with { Stats = new AgentStats("m", 1, 1, 1, 1, 6, 0, 0, null, TimeSpan.Zero) });

        var report = await Orchestrator(new FakeHost(), hardener, new FakeStryker(TwoGroupReport), configure: o => o.Agent.MaxAiCreditsPerRun = 5)
            .RunAsync(Arguments(approve: false), CancellationToken.None);

        Assert.Equal([GroupOutcome.Verified, GroupOutcome.Skipped], report.Repos.Single().Groups.Select(g => g.Outcome));
        Assert.Equal(1, hardener.Calls);
    }

    [Fact]
    public async Task RunAsync_PullRequestFailsAfterThePush_SaysTheBranchWasPushed()
    {
        var origin = await OriginAsync();
        var host = new FakeHost { FailCreate = true };

        var report = await Orchestrator(host, new FakeHardener(Verified)).RunAsync(Arguments(approve: true), CancellationToken.None);

        var repo = Assert.Single(report.Repos);
        Assert.Equal(HardenStatus.Failed, repo.Status);
        Assert.Contains($"pushed {repo.Branch}, but the pull request couldn't be opened", repo.Note, StringComparison.Ordinal);
        Assert.Contains(repo.Branch!, await origin.RunAsync("branch", "--format=%(refname:short)"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_TestingPlatformRepo_IsRefusedBeforeTheAgent()
    {
        var origin = await OriginAsync();
        origin.Write("global.json", "{ \"test\": { \"runner\": \"Microsoft.Testing.Platform\" } }");
        await origin.CommitAsync("mtp");
        var hardener = new FakeHardener(Verified);

        var report = await Orchestrator(new FakeHost(), hardener).RunAsync(Arguments(approve: true), CancellationToken.None);

        Assert.Equal(HardenStatus.Failed, report.Repos.Single().Status);
        Assert.Contains("Microsoft.Testing.Platform", report.Repos.Single().Note, StringComparison.Ordinal);
        Assert.Equal(0, hardener.Calls);
    }

    [Fact]
    public void Plan_TakesTheTopGroupsAcrossTargets()
    {
        var engine = new TargetConfig("Engine", "src/Engine/Engine.csproj", ["t/E.csproj"], null, [], []);
        var api = new TargetConfig("Api", "src/Api/Api.csproj", ["t/A.csproj"], null, [], []);
        var target = new RepoTarget("demo", null, "/r", "Demo.slnx", [engine, api]);
        var survey = new RepoSurvey
        {
            Name = "demo",
            Location = "/r",
            Status = SurveyStatus.Surveyed,
            Targets =
            [
                new TargetSurvey { Name = "Engine", Project = engine.Project, Groups = [PlanGroup("E.Small", 1, 0), PlanGroup("E.Strings", 5, 5)] },
                new TargetSurvey { Name = "Api", Project = api.Project, Groups = [PlanGroup("A.Big", 3, 0)] },
                new TargetSurvey { Name = "Broken", Project = "x.csproj", Failure = "failed", Groups = [PlanGroup("B.Huge", 9, 0)] },
            ],
        };

        var planned = HardenOrchestrator.Plan(target, survey, maxGroups: 2);

        Assert.Equal(["A.Big", "E.Small"], planned.Select(p => p.Group.Member.Name));
        Assert.Equal("Api", planned[0].Target.Name);
    }

    public void Dispose()
    {
        _origin?.Dispose();
        _output.Dispose();
        _work.Dispose();
    }

    private static HardenArguments Arguments(bool approve, bool dryRun = false) =>
        new([], null, dryRun, approve ? ApprovalPrompter.From((_, _) => true) : ApprovalPrompter.DeclineAll);

    private async Task<TempRepo> OriginAsync()
    {
        _origin = await TempRepo.CreateAsync();
        _origin.Write("Demo.slnx", "<Solution />")
            .Write("src/Demo/Demo.csproj", "<Project />")
            .Write("tests/Demo.Tests/Demo.Tests.csproj", "<Project />")
            .Write(Samples.CalculatorPath, Samples.Calculator)
            .Write(TestFile, "namespace Demo.Tests;\n\npublic class CalculatorTests\n{\n}\n");
        await _origin.CommitAsync("feat: demo");
        return _origin;
    }

    private HardenOrchestrator Orchestrator(
        IPullRequestHost? host, IGroupHardener hardener, FakeStryker? stryker = null, bool finalTestsPass = true, Action<TestHardenerOptions>? configure = null)
    {
        var options = new TestHardenerOptions
        {
            Repos =
            [
                new RepoOptions
                {
                    Name = "demo",
                    Path = _origin!.Path,
                    Solution = "Demo.slnx",
                    Targets = [new TargetOptions { Project = "src/Demo/Demo.csproj", TestProjects = ["tests/Demo.Tests/Demo.Tests.csproj"], CommitScope = "demo" }],
                },
            ],
            Output = new OutputOptions { Directory = _output.Path, WorkRoot = _work.Path },
        };
        configure?.Invoke(options);
        var config = ConfigLoader.Resolve(options, _output.Path);
        var processes = new FakeProcessRunner(call => Dotnet(call, finalTestsPass));
        var credentials = new AzureDevOpsCredentialProvider(new AzureDevOpsAuthOptions());
        var surveyor = new RepoSurveyor(
            config, new GitCli(new ProcessRunner()), processes, credentials, stryker ?? new FakeStryker(SurvivorReport), new SilentSurveyProgress(), _time);
        return new HardenOrchestrator(
            config, surveyor, new ScriptedBackends(), hardener, new DotnetCli(processes), new FakeHosts(host), new SilentHardenProgress(), _time);
    }

    private static ProcessResult Dotnet(ProcessCall call, bool testsPass)
    {
        if (call.Arguments[0] == "test")
        {
            var results = call.Arguments[call.Arguments.ToList().IndexOf("--results-directory") + 1];
            Directory.CreateDirectory(results);
            File.WriteAllText(Path.Combine(results, "results.trx"), $"""
                <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>
                <UnitTestResult testName="Demo.Tests.CalculatorTests.Add_Bigger_Sums" outcome="{(testsPass ? "Passed" : "Failed")}" />
                </Results></TestRun>
                """);
            return new ProcessResult(testsPass ? 0 : 1, "", "");
        }

        return FakeProcessRunner.Ok();
    }

    private static TestHardener.Analysis.SurvivorGroup PlanGroup(string name, int survivors, int strings) => new()
    {
        File = $"src/{name}.cs",
        Member = new TestHardener.Analysis.MemberInfo(name, "method", 1, 2, 0, 1, false, name),
        Survivors = Enumerable.Range(0, survivors)
            .Select(i => new TestHardener.Analysis.Survivor($"{i}", "m", new Location(new(1, 1), new(1, 2)), "a", "b", i < strings, []))
            .ToList(),
    };

    private static string TwoGroupReport(StrykerRequest request) =>
        new ReportBuilder(request.RepoRoot)
            .Test("t1", "Demo.Tests.CalculatorTests.Existing")
            .Mutant(Samples.CalculatorPath, Samples.Calculator, "a > b", MutantStatus.Survived, replacement: "a >= b")
            .Mutant(Samples.CalculatorPath, Samples.Calculator, "a > b", MutantStatus.Survived, replacement: "a < b")
            .Mutant(Samples.CalculatorPath, Samples.Calculator, "seed > 0", MutantStatus.Survived, replacement: "seed >= 0")
            .Json();

    private static string SurvivorReport(StrykerRequest request) =>
        new ReportBuilder(request.RepoRoot)
            .Test("t1", "Demo.Tests.CalculatorTests.Existing")
            .Mutant(Samples.CalculatorPath, Samples.Calculator, "a > b", MutantStatus.Survived, replacement: "a >= b")
            .Mutant(Samples.CalculatorPath, Samples.Calculator, "n * 2", MutantStatus.Killed, "Arithmetic mutation", "n / 2")
            .Json();

    private static async Task<GroupResult> Verified(GroupJob job)
    {
        await File.WriteAllTextAsync(Path.Combine(job.RepoRoot, TestFile), Written);
        var owned = new OwnedFile(TestFile, "tests/Demo.Tests/Demo.Tests.csproj", false, "it holds the covering tests");
        var verification = new Verification
        {
            OwnedPath = TestFile,
            Passed = true,
            NewTests = [new NewTest("Demo.Tests.CalculatorTests.Add_Bigger_Sums", "Add_Bigger_Sums", false)],
            Survivors = [new SurvivorOutcome("1", "Equality mutation", 12, Verification.KilledStatus, ["Demo.Tests.CalculatorTests.Add_Bigger_Sums"])],
            KillsPerTest = new Dictionary<string, int> { ["Demo.Tests.CalculatorTests.Add_Bigger_Sums"] = 1 },
        };
        return new GroupResult
        {
            Number = job.Number, Target = job.Target.Name, Group = job.Group, Outcome = GroupOutcome.Verified, Owned = owned, Rounds = [verification],
            Summary = new GroupSummary { Tests = [new TestClaim { Name = "Add_Bigger_Sums", Asserts = "sums @team" }], BlockedBy = BlockedBy.None },
        };
    }

    private static Task<GroupResult> Rejected(GroupJob job) =>
        Task.FromResult(new GroupResult { Number = job.Number, Target = job.Target.Name, Group = job.Group, Outcome = GroupOutcome.Rejected, Reason = "no kill" });

    private static AzureDevOpsPullRequest PullRequest(string status, DateTimeOffset created, string branch = "agent/test-hardening-x") =>
        new(7, "earlier", status, $"refs/heads/{branch}", "refs/heads/main", true, created, "https://example.invalid/pr/7");

    private sealed class FakeHardener(Func<GroupJob, Task<GroupResult>> result) : IGroupHardener
    {
        public int Calls { get; private set; }

        public Task<GroupResult> HardenAsync(GroupJob job, IAgentBackend backend, CancellationToken cancellationToken)
        {
            Calls++;
            return result(job);
        }
    }

    private sealed class FakeHost : IPullRequestHost
    {
        public IReadOnlyList<AzureDevOpsPullRequest> Existing { get; init; } = [];

        public bool FailCreate { get; init; }

        public List<(string Branch, string Target, string Title, string Description)> Created { get; } = [];

        public Task<IReadOnlyList<AzureDevOpsPullRequest>> ListAsync(string branchPrefix, CancellationToken cancellationToken) => Task.FromResult(Existing);

        public Task<AzureDevOpsPullRequest> CreateDraftAsync(string branch, string targetBranch, string title, string description, CancellationToken cancellationToken)
        {
            if (FailCreate)
            {
                throw new HttpRequestException("503 from Azure DevOps");
            }

            Created.Add((branch, targetBranch, title, description));
            return Task.FromResult(new AzureDevOpsPullRequest(1, title, "active", branch, targetBranch, true, DateTimeOffset.UnixEpoch, "https://example.invalid/pr/1"));
        }
    }

    private sealed class FakeHosts(IPullRequestHost? host) : IPullRequestHosts
    {
        public IPullRequestHost? For(RepoTarget target, AzureDevOpsCredential? credential) => host;
    }

    private sealed class ScriptedBackends : IAgentBackendFactory
    {
        public IAgentBackend Create() => new ScriptedBackend();

        public Task EnsureReadyAsync(IAgentBackend backend, string workingDirectory, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class SilentHardenProgress : IHardenProgress
    {
        public void RepoStarted(RepoTarget target, int index, int count)
        {
        }

        public void GroupStarted(int number, int count, PlannedGroup planned)
        {
        }

        public void GroupFinished(GroupResult result)
        {
        }

        public void RepoFinished(RepoHardenReport report)
        {
        }

        public void RunFinished(HardenReport report, string reportPath)
        {
        }
    }
}
