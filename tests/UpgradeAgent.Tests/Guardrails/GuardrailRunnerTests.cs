using UpgradeAgent.Build;
using UpgradeAgent.Guardrails;
using UpgradeAgent.Tests.TestSupport;

namespace UpgradeAgent.Tests.Guardrails;

/// <summary>
/// "Bad agent" scenarios against a real git repo: the app bumps, the agent makes a change, the
/// guardrails must accept honest fixes and reject every shortcut.
/// </summary>
public sealed class GuardrailRunnerTests : IAsyncLifetime
{
    private const string TestKey = "App.Tests [net10.0] App.Tests.CodeTests.Works";

    private TempRepo _repo = null!;
    private GuardrailRunner _runner = null!;
    private Baseline _baseline = null!;
    private GroupStartState _start = null!;

    public async Task InitializeAsync()
    {
        _repo = await TempRepo.CreateAsync();
        _runner = new GuardrailRunner(
            _repo.Git,
            [new GitStateGuardrail(), new BuildGuardrail(), new TestsGuardrail(), new SuppressionGuardrail(), new BuildSettingsGuardrail(), new FilesGuardrail()],
            [new TestFileNotes(_repo.Git), new PublicApiNotes(), new ClaimNotes()]);
        _repo
            .Write(".gitignore", "bin/\nobj/\n*.user\n")
            .Write("Directory.Packages.props", """<Project><ItemGroup><PackageVersion Include="Foo" Version="1.0.0" /></ItemGroup></Project>""")
            .Write("src/App/App.csproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <NoWarn>CS1591</NoWarn>
                  </PropertyGroup>
                  <ItemGroup><PackageReference Include="Foo" /></ItemGroup>
                </Project>
                """)
            .Write("src/App/Code.cs", "class Code { string M() => Formatter.Format(1m); }\n")
            .Write("tests/App.Tests/App.Tests.csproj", """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Microsoft.NET.Test.Sdk" /></ItemGroup></Project>""")
            .Write("tests/App.Tests/CodeTests.cs", "class CodeTests { [Fact] void Works() { Assert.Equal(1, 1); Assert.True(true); } }\n");
        var commit = await _repo.CommitAsync("initial");

        _baseline = new Baseline(
            commit, "10.0.112", TestRunnerMode.VSTest, Inventory(passed: 1), null,
            TestProjects.FindTestFiles(_repo.Path, await _repo.Git.ListFilesAsync(_repo.Path)).ToList(),
            DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task HonestFixPasses()
    {
        await BumpAsync();
        _repo.Write("src/App/Code.cs", "class Code { string M() => Formatter.FormatValue(1m); }\n");

        var report = await RunAsync();

        Assert.True(report.Passed, report.FailureSummary);
        Assert.Empty(report.Notes);
    }

    [Fact]
    public async Task PragmaSuppressionIsRejected()
    {
        await BumpAsync();
        _repo.Write("src/App/Code.cs", "#pragma warning disable CS0618\nclass Code { string M() => Formatter.Format(1m); }\n");

        await AssertRejectedAsync("No suppressions or skips", "#pragma warning disable");
    }

    [Fact]
    public async Task SkippedTestIsRejectedAndTheTestFileIsFlagged()
    {
        await BumpAsync();
        _repo.Write("tests/App.Tests/CodeTests.cs", "class CodeTests { [Fact(Skip = \"later\")] void Works() { Assert.Equal(1, 1); Assert.True(true); } }\n");

        var report = await AssertRejectedAsync("No suppressions or skips", "Skip =");
        Assert.Contains("test file modified: tests/App.Tests/CodeTests.cs", report.Notes.Select(n => n.Message));
    }

    [Fact]
    public async Task ReindentedPreexistingNoWarnIsAccepted()
    {
        await BumpAsync();
        _repo.Write("src/App/App.csproj", _repo.Read("src/App/App.csproj").Replace("    <NoWarn>", "      <NoWarn>", StringComparison.Ordinal));

        var report = await RunAsync();

        Assert.True(report.Passed, report.FailureSummary);
    }

    [Fact]
    public async Task DeletedTestFileIsRejected()
    {
        await BumpAsync();
        _repo.Delete("tests/App.Tests/CodeTests.cs");

        await AssertRejectedAsync("Files", "deleted test file tests/App.Tests/CodeTests.cs");
    }

    [Fact]
    public async Task NewIgnoredProjectUserFileIsRejected()
    {
        await BumpAsync();
        _repo.Write("src/App/App.csproj.user", "<Project><PropertyGroup><TreatWarningsAsErrors>false</TreatWarningsAsErrors></PropertyGroup></Project>");

        await AssertRejectedAsync("Files", "new git-ignored file src/App/App.csproj.user");
    }

    [Fact]
    public async Task AgentCommitIsRejected()
    {
        await BumpAsync();
        _repo.Write("src/App/Code.cs", "class Code { }\n");
        await _repo.CommitAsync("agent sneaks a commit");

        await AssertRejectedAsync("Git state", "HEAD moved");
    }

    [Fact]
    public async Task VersionChangeAfterTheBumpIsRejected()
    {
        await BumpAsync();
        _repo.Write("Directory.Packages.props", """<Project><ItemGroup><PackageVersion Include="Foo" Version="1.0.5" /><PackageVersion Include="Transitive.Pin" Version="1.0.0" /></ItemGroup></Project>""");

        var report = await AssertRejectedAsync("Package versions and build settings", "Foo");
        Assert.Contains("Transitive.Pin", report.Checks.Single(c => c.Name == "Package versions and build settings").Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TargetFrameworkChangeIsRejected()
    {
        await BumpAsync();
        _repo.Write("src/App/App.csproj", _repo.Read("src/App/App.csproj").Replace("net10.0", "net11.0", StringComparison.Ordinal));

        await AssertRejectedAsync("Package versions and build settings", "TargetFramework");
    }

    [Fact]
    public async Task DroppedAssertionsWarnButDoNotReject()
    {
        await BumpAsync();
        _repo.Write("tests/App.Tests/CodeTests.cs", "class CodeTests { [Fact] void Works() { Assert.Equal(1, 1); } }\n");

        var report = await RunAsync();

        Assert.True(report.Passed, report.FailureSummary);
        Assert.Contains("assertion count dropped in tests/App.Tests/CodeTests.cs: 2 → 1", report.Notes.Select(n => n.Message));
    }

    [Fact]
    public async Task MissingTestMethodIsRejected()
    {
        await BumpAsync();

        var report = await RunAsync(tests: new TestRunResult(true, new TestInventory(new Dictionary<string, MethodStats>()), null, "", TimeSpan.Zero));

        Assert.False(report.Checks.Single(c => c.Name == "Tests").Passed);
    }

    [Fact]
    public void CountOnlyFallbackIsLabelledWeaker()
    {
        var baseline = _baseline with { Tests = null, Counts = new TestCounts(3, 3, 0, 0) };

        var check = TestsGuardrail.Evaluate(baseline, new TestRunResult(true, null, new TestCounts(3, 3, 0, 0), "", TimeSpan.Zero));

        Assert.True(check.Passed);
        Assert.Contains("weaker", check.Detail, StringComparison.Ordinal);
    }

    public Task DisposeAsync()
    {
        _repo.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>The app's bump happens before the start state is captured, exactly as in a run.</summary>
    private async Task BumpAsync()
    {
        _repo.Write("Directory.Packages.props", _repo.Read("Directory.Packages.props").Replace("1.0.0", "2.0.0", StringComparison.Ordinal));
        _start = await _runner.CaptureStartAsync(_repo.Path, _baseline.Commit, CancellationToken.None);
    }

    private Task<GuardrailReport> RunAsync(TestRunResult? tests = null) =>
        _runner.RunAsync(
            new GuardrailInput(
                _repo.Path,
                _start,
                _baseline,
                TestData.Build(),
                tests ?? new TestRunResult(true, Inventory(passed: 1), null, "", TimeSpan.Zero),
                new ReviewContext(null, [])),
            CancellationToken.None);

    private async Task<GuardrailReport> AssertRejectedAsync(string check, string detail)
    {
        var report = await RunAsync();
        var failed = report.Checks.Single(c => c.Name == check);
        Assert.False(report.Passed);
        Assert.False(failed.Passed);
        Assert.Contains(detail, failed.Detail, StringComparison.Ordinal);
        return report;
    }

    private static TestInventory Inventory(int passed) =>
        new(new Dictionary<string, MethodStats> { [TestKey] = new(passed, 0, 0) });
}
