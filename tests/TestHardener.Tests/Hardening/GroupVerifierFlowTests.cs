using RepoKit;
using TestHardener.Analysis;
using TestHardener.Config;
using TestHardener.Hardening;
using TestHardener.Infrastructure;
using TestHardener.Stryker;
using TestHardener.Tests.TestSupport;

namespace TestHardener.Tests.Hardening;

public sealed class GroupVerifierFlowTests : IDisposable
{
    private const string TestFile = "tests/Demo.Tests/CalculatorTests.cs";
    private const string TestProject = "tests/Demo.Tests/Demo.Tests.csproj";
    private const string NewTestName = "Demo.Tests.CalculatorTests.Add_Bigger_Sums";
    private const string Snapshot = "namespace Demo.Tests;\n\npublic class CalculatorTests\n{\n}\n";

    private static readonly TargetConfig Target = new("Demo", "src/Demo/Demo.csproj", [TestProject], null, [], []);

    private readonly TempDirectory _output = new("th-vflow-");
    private TempRepo? _repo;

    [Fact]
    public async Task VerifyAsync_GoodTest_Passes()
    {
        var repo = await RepoAsync();
        repo.Write(TestFile, WithTests("Add_Bigger_Sums"));

        var result = await Verifier(Passing(NewTestName), KilledBy(NewTestName)).VerifyAsync(await RequestAsync(repo), CancellationToken.None);

        Assert.True(result.Passed, result.Feedback);
        Assert.Equal(1, result.Killed);
        Assert.Equal(1, result.KillsPerTest[NewTestName]);
    }

    [Fact]
    public async Task VerifyAsync_OtherChanges_AreUndone()
    {
        var repo = await RepoAsync();
        repo.Write(TestFile, WithTests("Add_Bigger_Sums"))
            .Write(Samples.CalculatorPath, "changed")
            .Write("tests/Demo.Tests/Helpers.cs", "new file")
            .Write("tests/Demo.Tests/KeptTests.cs", "edited after it was kept");
        var request = await RequestAsync(repo, new Dictionary<string, string> { ["tests/Demo.Tests/KeptTests.cs"] = "kept content" });

        var result = await Verifier(Passing(NewTestName), KilledBy(NewTestName)).VerifyAsync(request, CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("Helpers.cs", result.Feedback, StringComparison.Ordinal);
        Assert.Equal(Samples.Calculator, repo.Read(Samples.CalculatorPath));
        Assert.False(File.Exists(Path.Combine(repo.Path, "tests/Demo.Tests/Helpers.cs")));
        Assert.Equal("kept content", repo.Read("tests/Demo.Tests/KeptTests.cs"));
    }

    [Fact]
    public async Task VerifyAsync_TestFailingOnALaterRun_IsFlaky()
    {
        var repo = await RepoAsync();
        repo.Write(TestFile, WithTests("Add_Bigger_Sums"));
        var runs = 0;

        var result = await Verifier(call => (++runs == 1 ? Trx((NewTestName, "Passed")) : Trx((NewTestName, "Failed")))(call), KilledBy(NewTestName))
            .VerifyAsync(await RequestAsync(repo), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("flaky", result.Feedback, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyAsync_NewTestThatDidNotRun_IsNamed()
    {
        var repo = await RepoAsync();
        repo.Write(TestFile, WithTests("Add_Bigger_Sums"));

        var result = await Verifier(Passing("Demo.Tests.CalculatorTests.Something_Else"), KilledBy(NewTestName)).VerifyAsync(await RequestAsync(repo), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains($"didn't run: {NewTestName}", result.Feedback, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyAsync_TestThatKillsNothing_IsSentBack()
    {
        var repo = await RepoAsync();
        repo.Write(TestFile, WithTests("Add_Bigger_Sums", "Add_Other_Sums"));
        var other = "Demo.Tests.CalculatorTests.Add_Other_Sums";

        var result = await Verifier(Passing(NewTestName, other), KilledBy(NewTestName)).VerifyAsync(await RequestAsync(repo), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains($"catch none of the listed mutants; change them so they do, or remove them: {other}", result.Feedback, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyAsync_MutantNotReached_SaysSo()
    {
        var repo = await RepoAsync();
        repo.Write(TestFile, WithTests("Add_Bigger_Sums"));
        var report = new ReportBuilder(repo.Path)
            .Test("t1", NewTestName)
            .Mutant(Samples.CalculatorPath, Samples.Calculator, "a > b", MutantStatus.NoCoverage, replacement: "a >= b")
            .Json();

        var result = await Verifier(Passing(NewTestName), _ => report).VerifyAsync(await RequestAsync(repo), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("your tests don't execute this code", result.Feedback, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyAsync_StrykerFailure_FailsTheCheck()
    {
        var repo = await RepoAsync();
        repo.Write(TestFile, WithTests("Add_Bigger_Sums"));

        var result = await Verifier(Passing(NewTestName), _ => null).VerifyAsync(await RequestAsync(repo), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("Stryker couldn't check the mutants: fake failure", result.Feedback, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyAsync_TestsThatWriteFiles_AreCaughtAfterTheyRun()
    {
        var repo = await RepoAsync();
        repo.Write(TestFile, WithTests("Add_Bigger_Sums"));

        var result = await Verifier(
            call =>
            {
                File.WriteAllText(Path.Combine(repo.Path, "written-by-a-test.txt"), "x");
                return Passing(NewTestName)(call);
            },
            KilledBy(NewTestName)).VerifyAsync(await RequestAsync(repo), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("Running your tests changed other files", result.Feedback, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(repo.Path, "written-by-a-test.txt")));
    }

    [Fact]
    public async Task VerifyAsync_StaticProblems_StopBeforeTheBuild()
    {
        var repo = await RepoAsync();
        repo.Write(TestFile, Snapshot.Replace("{\n}", "{\n    [Fact]\n    public void Add_Bigger_Sums() { }\n}", StringComparison.Ordinal));
        var builds = 0;

        var result = await Verifier(call => { builds++; return Passing(NewTestName)(call); }, KilledBy(NewTestName)).VerifyAsync(await RequestAsync(repo), CancellationToken.None);

        Assert.False(result.Passed);
        Assert.Contains("has no assertion", result.Feedback, StringComparison.Ordinal);
        Assert.Equal(0, builds);
    }

    [Fact]
    public void ParseStatus_ReadsNulSeparatedPaths() =>
        Assert.Equal(["src/Ü nicode.cs", "new file.txt"], GroupVerifier.ParseStatus(" M src/Ü nicode.cs\0?? new file.txt\0"));

    public void Dispose()
    {
        _repo?.Dispose();
        _output.Dispose();
    }

    private static string WithTests(params string[] names) =>
        Snapshot.Replace("{\n}", "{\n" + string.Join("\n", names.Select(n => $"    [Fact]\n    public void {n}() => Assert.Equal(3, 3);\n")) + "}", StringComparison.Ordinal);

    private async Task<TempRepo> RepoAsync()
    {
        _repo = await TempRepo.CreateAsync();
        _repo.Write(Samples.CalculatorPath, Samples.Calculator)
            .Write(TestProject, "<Project />")
            .Write(TestFile, Snapshot)
            .Write("tests/Demo.Tests/KeptTests.cs", "original");
        await _repo.CommitAsync();
        return _repo;
    }

    private async Task<VerifyRequest> RequestAsync(TempRepo repo, IReadOnlyDictionary<string, string>? kept = null)
    {
        var member = MemberLocator.Parse(Samples.Calculator).Locate(SourceLocation.Of(Samples.Calculator, "a > b")).Member!;
        var survivor = new Survivor("1", "Equality mutation", SourceLocation.Of(Samples.Calculator, "a > b"), "a > b", "a >= b", false, []);
        var group = new SurvivorGroup { File = Samples.CalculatorPath, Member = member, Survivors = [survivor] };
        var tracked = await repo.Git.ListFilesAsync(repo.Path);
        var job = new GroupJob(1, "demo", repo.Path, repo.Git, Target, TestRunnerMode.VSTest, group, tracked, kept ?? new Dictionary<string, string>(),
            [], null, _output.Path, new Dictionary<string, string?>());
        return new VerifyRequest(job, [survivor], new OwnedFile(TestFile, TestProject, false, "test"), Snapshot, _output.Path);
    }

    private static GroupVerifier Verifier(Func<ProcessCall, ProcessResult> tests, Func<StrykerRequest, string?> stryker)
    {
        var runner = new FakeProcessRunner(call => call.Arguments[0] == "test" ? tests(call) : FakeProcessRunner.Ok());
        return new GroupVerifier(new DotnetCli(runner), new FakeStryker(stryker), new HardeningOptions { OriginalRuns = 2 });
    }

    private static Func<ProcessCall, ProcessResult> Passing(params string[] names) => Trx(names.Select(n => (n, "Passed")).ToArray());

    private static Func<ProcessCall, ProcessResult> Trx(params (string Name, string Outcome)[] results) => call =>
    {
        var folder = call.Arguments[call.Arguments.ToList().IndexOf("--results-directory") + 1];
        Directory.CreateDirectory(folder);
        var rows = string.Join("\n", results.Select(r => $"<UnitTestResult testName=\"{r.Name}\" outcome=\"{r.Outcome}\" />"));
        File.WriteAllText(Path.Combine(folder, "results.trx"),
            $"<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\"><Results>{rows}</Results></TestRun>");
        return new ProcessResult(results.All(r => r.Outcome == "Passed") ? 0 : 1, "", "");
    };

    private static Func<StrykerRequest, string?> KilledBy(string testName) => request =>
        new ReportBuilder(request.RepoRoot)
            .Test("t1", testName)
            .Mutant(Samples.CalculatorPath, Samples.Calculator, "a > b", MutantStatus.Killed, replacement: "a >= b", killedBy: ["t1"])
            .Json();
}
