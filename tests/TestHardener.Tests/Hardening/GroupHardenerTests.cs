using AgentHarness.Testing;
using Microsoft.Extensions.Time.Testing;
using TestHardener.Analysis;
using TestHardener.Config;
using TestHardener.Hardening;
using TestHardener.Infrastructure;
using TestHardener.Tests.TestSupport;

namespace TestHardener.Tests.Hardening;

public sealed class GroupHardenerTests : IDisposable
{
    private const string TestFile = "tests/Demo.Tests/CalculatorTests.cs";
    private const string Original = "namespace Demo.Tests;\n\npublic class CalculatorTests\n{\n}\n";
    private const string Written = "namespace Demo.Tests;\n\npublic class CalculatorTests\n{\n    [Fact]\n    public void Add_Bigger_Sums() => Assert.Equal(3, 3);\n}\n";

    private static readonly TargetConfig Target = new("Demo", "src/Demo/Demo.csproj", ["tests/Demo.Tests/Demo.Tests.csproj"], null, [], []);

    private readonly TempDirectory _output = new("th-group-");
    private TempRepo? _repo;

    [Fact]
    public async Task HardenAsync_PassingFirstRound_IsVerified()
    {
        var repo = await RepoAsync();
        var verifier = new FakeVerifier(Passed());
        var backend = new ScriptedBackend()
            .Turn(t => t.Edit(TestFile, Written).Reply("Added a test."))
            .Turn(t => t.ReplyJson(Summary()));

        var result = await Hardener(verifier).HardenAsync(Job(repo), backend, CancellationToken.None);

        Assert.Equal(GroupOutcome.Verified, result.Outcome);
        Assert.Equal(TestFile, result.Owned!.Path);
        Assert.Single(result.Rounds);
        Assert.Equal("Add_Bigger_Sums", result.Summary!.Tests.Single().Name);
        Assert.Equal(Written, repo.Read(TestFile));
        Assert.Equal(Original, verifier.Requests.Single().Snapshot);
    }

    [Fact]
    public async Task HardenAsync_FailedRound_SendsTheFeedbackAndTriesAgain()
    {
        var repo = await RepoAsync();
        var verifier = new FakeVerifier(Failed("the build failed: CS1002"), Passed());
        var backend = new ScriptedBackend()
            .Turn(t => t.Edit(TestFile, "broken").Reply("Added a test."))
            .Turn(t => t.Edit(TestFile, Written).Reply("Fixed it."))
            .Turn(t => t.ReplyJson(Summary()));

        var result = await Hardener(verifier).HardenAsync(Job(repo), backend, CancellationToken.None);

        Assert.Equal(GroupOutcome.Verified, result.Outcome);
        Assert.Equal(2, result.Rounds.Count);
        Assert.Contains("the build failed: CS1002", backend.Messages[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task HardenAsync_EveryRoundFails_IsRejectedAndTheFileRestored()
    {
        var repo = await RepoAsync();
        var verifier = new FakeVerifier(Failed("no kill"), Failed("no kill"), Failed("still no kill"));
        var backend = new ScriptedBackend()
            .Turn(t => t.Edit(TestFile, Written).Reply("one"))
            .Turn(t => t.Edit(TestFile, Written).Reply("two"))
            .Turn(t => t.Edit(TestFile, Written).Reply("three"))
            .Turn(t => t.ReplyJson(Summary()));

        var result = await Hardener(verifier).HardenAsync(Job(repo), backend, CancellationToken.None);

        Assert.Equal(GroupOutcome.Rejected, result.Outcome);
        Assert.Equal(3, result.Rounds.Count);
        Assert.Equal("still no kill", result.Reason);
        Assert.Equal(Original, repo.Read(TestFile));
    }

    [Fact]
    public async Task HardenAsync_WritingAnotherFile_IsRefused()
    {
        var repo = await RepoAsync();
        var backend = new ScriptedBackend()
            .Turn(t => t.Edit("src/Demo/Calculator.cs", "changed").Edit(TestFile, Written).Reply("done"))
            .Turn(t => t.ReplyJson(Summary()));

        await Hardener(new FakeVerifier(Passed())).HardenAsync(Job(repo), backend, CancellationToken.None);

        Assert.False(backend.Decisions[0].Allowed);
        Assert.True(backend.Decisions[1].Allowed);
        Assert.Equal(Samples.Calculator, repo.Read(Samples.CalculatorPath));
    }

    [Fact]
    public async Task HardenAsync_NoTestFileYet_StartsASkeletonAndDeletesItOnRejection()
    {
        var repo = await RepoAsync(withTestFile: false);
        var skeletonPath = "tests/Demo.Tests/CalculatorTests.cs";
        string? seen = null;
        var verifier = new FakeVerifier(Failed("no kill")) { OnVerify = r => seen = r.Snapshot };
        var backend = new ScriptedBackend()
            .Turn(t => t.Reply("gave up"))
            .Turn(t => t.ReplyJson(Summary()));

        var result = await Hardener(verifier, maxRounds: 1).HardenAsync(Job(repo, coveringFiles: []), backend, CancellationToken.None);

        Assert.True(result.Owned!.IsNew);
        Assert.Equal(skeletonPath, result.Owned.Path);
        Assert.Contains("public class CalculatorTests", seen, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(repo.Path, skeletonPath)));
    }

    [Fact]
    public async Task HardenAsync_ThePromptNamesTheSurvivorsAndTheOwnedFile()
    {
        var repo = await RepoAsync();
        var backend = new ScriptedBackend()
            .Turn(t => t.Edit(TestFile, Written).Reply("done"))
            .Turn(t => t.ReplyJson(Summary()));

        await Hardener(new FakeVerifier(Passed())).HardenAsync(Job(repo), backend, CancellationToken.None);

        Assert.Contains("`a > b` becomes `a >= b`", backend.Messages[0], StringComparison.Ordinal);
        Assert.Contains(TestFile, backend.Messages[0], StringComparison.Ordinal);
        Assert.Contains("public int Add(int a, int b)", backend.Messages[0], StringComparison.Ordinal);
    }

    public void Dispose()
    {
        _repo?.Dispose();
        _output.Dispose();
    }

    private async Task<TempRepo> RepoAsync(bool withTestFile = true)
    {
        _repo = await TempRepo.CreateAsync();
        _repo.Write(Samples.CalculatorPath, Samples.Calculator).Write("tests/Demo.Tests/Demo.Tests.csproj", "<Project />");
        if (withTestFile)
        {
            _repo.Write(TestFile, Original);
        }

        await _repo.CommitAsync();
        return _repo;
    }

    private GroupJob Job(TempRepo repo, IReadOnlyList<string>? coveringFiles = null)
    {
        var source = Samples.Calculator;
        var member = MemberLocator.Parse(source).Locate(SourceLocation.Of(source, "a > b")).Member!;
        var group = new SurvivorGroup
        {
            File = Samples.CalculatorPath,
            Member = member,
            Survivors = [new Survivor("1", "Equality mutation", SourceLocation.Of(source, "a > b"), "a > b", "a >= b", false, ["Demo.Tests.CalculatorTests.Existing"])],
            CoveringTests = ["Demo.Tests.CalculatorTests.Existing"],
            CoveringTestFiles = coveringFiles ?? [TestFile],
        };
        var tracked = Directory.EnumerateFiles(repo.Path, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(repo.Path, f).Replace('\\', '/'))
            .Where(f => !f.StartsWith(".git/", StringComparison.Ordinal))
            .ToList();
        return new GroupJob(1, "demo", repo.Path, repo.Git, Target, TestRunnerMode.VSTest, group, tracked, new Dictionary<string, string>(),
            [], null, _output.Path, new Dictionary<string, string?>());
    }

    private static GroupHardener Hardener(IGroupVerifier verifier, int maxRounds = 3)
    {
        var options = new TestHardenerOptions { Hardening = new HardeningOptions { MaxRounds = maxRounds } };
        var config = new ResolvedConfig(options, [], "/out", "/work", "/tools");
        var dotnet = new DotnetCli(new FakeProcessRunner(_ => FakeProcessRunner.Ok()));
        return new GroupHardener(verifier, dotnet, config, new FakeTimeProvider());
    }

    private static Verification Passed() => new()
    {
        OwnedPath = TestFile,
        Passed = true,
        NewTests = [new NewTest("Demo.Tests.CalculatorTests.Add_Bigger_Sums", "Add_Bigger_Sums", false)],
        Survivors = [new SurvivorOutcome("1", "Equality mutation", 12, Verification.KilledStatus, ["Demo.Tests.CalculatorTests.Add_Bigger_Sums"])],
    };

    private static Verification Failed(string feedback) => new() { OwnedPath = TestFile, Passed = false, Feedback = feedback };

    private static GroupSummary Summary() => new()
    {
        Tests = [new TestClaim { Name = "Add_Bigger_Sums", Asserts = "Add sums when a is bigger", Kills = ["1"] }],
        BlockedBy = BlockedBy.None,
    };

    private sealed class FakeVerifier(params Verification[] results) : IGroupVerifier
    {
        private int _next;

        public List<VerifyRequest> Requests { get; } = [];

        public Action<VerifyRequest>? OnVerify { get; init; }

        public Task<Verification> VerifyAsync(VerifyRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            OnVerify?.Invoke(request);
            return Task.FromResult(results[Math.Min(_next++, results.Length - 1)]);
        }
    }
}
