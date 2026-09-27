using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using RepoKit;
using RepoKit.AzureDevOps;
using TestHardener.Config;
using TestHardener.Reporting;
using TestHardener.Run;
using TestHardener.Stryker;
using TestHardener.Tests.TestSupport;

namespace TestHardener.Tests.Run;

public sealed class SurveyOrchestratorTests : IDisposable
{
    private readonly TempDirectory _output = new("th-out-");
    private readonly TempDirectory _work = new("th-work-");
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task RunAsync_SurveysEachTargetAndWritesTheRepoSurvey()
    {
        using var repo = await DemoRepoAsync();
        var sha = await repo.Git.HeadAsync(repo.Path);
        var stryker = new FakeStryker(SurvivorReport);

        var report = await Orchestrator(repo, stryker).RunAsync(new SurveyArguments([], null), CancellationToken.None);

        var survey = Assert.Single(report.Repos);
        Assert.Equal(SurveyStatus.Surveyed, survey.Status);
        Assert.Equal(sha, survey.Sha);
        var target = Assert.Single(survey.Targets);
        Assert.Equal("Calculator.Add(int, int)", Assert.Single(target.Groups).Member.Name);
        Assert.Equal("stryker/Demo/reports/mutation-report.json", target.ReportPath);
        Assert.True(File.Exists(Path.Combine(report.OutputDirectory, "demo", SurveyReportWriter.RepoFileName)));
        Assert.True(File.Exists(Path.Combine(report.OutputDirectory, "report.md")));
        Assert.False(Directory.Exists(survey.CloneRoot));
    }

    [Fact]
    public async Task RunAsync_From_ReusesTheReportAtTheSurveyedCommit()
    {
        using var repo = await DemoRepoAsync();
        var first = await Orchestrator(repo, new FakeStryker(SurvivorReport)).RunAsync(new SurveyArguments([], null), CancellationToken.None);
        repo.Write(Samples.CalculatorPath, "\n\n\n" + Samples.Calculator);
        await repo.CommitAsync("shift every line");
        _time.Advance(TimeSpan.FromMinutes(1));

        var second = await Orchestrator(repo, new FakeStryker(_ => throw new InvalidOperationException("Stryker must not run"))).RunAsync(
            new SurveyArguments([], first.OutputDirectory), CancellationToken.None);

        var survey = Assert.Single(second.Repos);
        Assert.Equal(first.Repos[0].Sha, survey.Sha);
        Assert.Equal("Calculator.Add(int, int)", Assert.Single(Assert.Single(survey.Targets).Groups).Member.Name);
        Assert.True(File.Exists(Path.Combine(second.OutputDirectory, "demo", "stryker", "Demo", StrykerRunner.ReportRelativePath)));
    }

    [Fact]
    public async Task RunAsync_OneTargetFails_TheOthersStillRun()
    {
        using var repo = await DemoRepoAsync();
        var stryker = new FakeStryker(request => request.Target.Name == "Broken" ? null : SurvivorReport(request));
        var extra = new TargetOptions { Project = "src/Broken/Broken.csproj", TestProjects = ["tests/Demo.Tests/Demo.Tests.csproj"] };

        var report = await Orchestrator(repo, stryker, extra).RunAsync(new SurveyArguments([], null), CancellationToken.None);

        var survey = Assert.Single(report.Repos);
        Assert.Equal(SurveyStatus.Partial, survey.Status);
        Assert.Null(survey.Targets[0].Failure);
        Assert.Equal("fake failure", survey.Targets[1].Failure);
    }

    [Fact]
    public async Task RunAsync_RestoreFails_TheRepoFails()
    {
        using var repo = await DemoRepoAsync();
        var processes = new FakeProcessRunner(_ => new ProcessResult(1, "", "restore broke"));

        var report = await Orchestrator(repo, new FakeStryker(SurvivorReport), processes: processes).RunAsync(new SurveyArguments([], null), CancellationToken.None);

        var survey = Assert.Single(report.Repos);
        Assert.Equal(SurveyStatus.Failed, survey.Status);
        Assert.Contains("restore broke", survey.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_RestoreAndStryker_GetAnEnvironmentWithoutSecrets()
    {
        using var repo = await DemoRepoAsync();
        var processes = new FakeProcessRunner(_ => FakeProcessRunner.Ok());
        var stryker = new FakeStryker(SurvivorReport);

        await Orchestrator(repo, stryker, processes: processes).RunAsync(new SurveyArguments([], null), CancellationToken.None);

        Assert.Equal(["restore", "Demo.slnx", "-tl:off", "-nologo"], processes.Calls.Single().Arguments);
        Assert.Equal("en", processes.Calls.Single().Environment!["DOTNET_CLI_UI_LANGUAGE"]);
        Assert.True(processes.Calls.Single().Environment!.ContainsKey("ADO_PAT"));
        Assert.Null(processes.Calls.Single().Environment!["ADO_PAT"]);
        Assert.Same(processes.Calls.Single().Environment, stryker.Environments.Single());
    }

    [Fact]
    public async Task RunAsync_WritesSurveyJsonThatRoundTrips()
    {
        using var repo = await DemoRepoAsync();

        var report = await Orchestrator(repo, new FakeStryker(SurvivorReport)).RunAsync(new SurveyArguments([], null), CancellationToken.None);

        var json = await File.ReadAllTextAsync(Path.Combine(report.OutputDirectory, "demo", SurveyReportWriter.RepoFileName));
        var read = JsonSerializer.Deserialize<RepoSurvey>(json, SurveyReportWriter.Json)!;
        Assert.Equal(json, JsonSerializer.Serialize(read, SurveyReportWriter.Json));
    }

    public void Dispose()
    {
        _output.Dispose();
        _work.Dispose();
    }

    private static string SurvivorReport(StrykerRequest request) =>
        new ReportBuilder(request.RepoRoot)
            .Test("t1", "Demo.Tests.CalculatorTests.Add_Works")
            .Mutant(Samples.CalculatorPath, Samples.Calculator, "a > b", MutantStatus.Survived, replacement: "a >= b")
            .Mutant(Samples.CalculatorPath, Samples.Calculator, "s.Length > 3", MutantStatus.Killed)
            .Json();

    private static async Task<TempRepo> DemoRepoAsync()
    {
        var repo = await TempRepo.CreateAsync();
        repo.Write("Demo.slnx", "<Solution />")
            .Write("src/Demo/Demo.csproj", "<Project />")
            .Write(Samples.CalculatorPath, Samples.Calculator);
        await repo.CommitAsync("fix: first");
        return repo;
    }

    private SurveyOrchestrator Orchestrator(TempRepo repo, FakeStryker stryker, TargetOptions? extraTarget = null, FakeProcessRunner? processes = null)
    {
        var options = new TestHardenerOptions
        {
            Repos =
            [
                new RepoOptions
                {
                    Name = "demo",
                    Path = repo.Path,
                    Solution = "Demo.slnx",
                    Targets =
                    [
                        new TargetOptions { Project = "src/Demo/Demo.csproj", TestProjects = ["tests/Demo.Tests/Demo.Tests.csproj"] },
                        .. extraTarget is null ? Array.Empty<TargetOptions>() : [extraTarget],
                    ],
                },
            ],
            Output = new OutputOptions { Directory = _output.Path, WorkRoot = _work.Path },
        };
        var config = ConfigLoader.Resolve(options, _output.Path);
        var progress = new SilentProgress();
        var surveyor = new RepoSurveyor(
            config,
            new GitCli(new ProcessRunner()),
            processes ?? new FakeProcessRunner(_ => FakeProcessRunner.Ok()),
            new AzureDevOpsCredentialProvider(new AzureDevOpsAuthOptions { PatEnvVar = "ADO_PAT" }),
            stryker,
            progress,
            _time);
        return new SurveyOrchestrator(config, surveyor, progress, _time);
    }

    private sealed class FakeStryker(Func<StrykerRequest, string?> report) : IStrykerRunner
    {
        public List<IReadOnlyDictionary<string, string?>> Environments { get; } = [];

        public async Task<StrykerRun> RunAsync(
            StrykerRequest request, string outputDirectory, IReadOnlyDictionary<string, string?> environment, CancellationToken cancellationToken)
        {
            Environments.Add(environment);
            if (report(request) is not { } json)
            {
                return new StrykerRun(null, outputDirectory, TimeSpan.Zero, "fake failure");
            }

            var path = Path.Combine(outputDirectory, StrykerRunner.ReportRelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, json, cancellationToken);
            return new StrykerRun(MutationReportParser.Parse(json, request.RepoRoot), outputDirectory, TimeSpan.Zero, null);
        }
    }

    private sealed class SilentProgress : ISurveyProgress
    {
        public void RepoStarted(RepoTarget target, int index, int count)
        {
        }

        public void TargetStarted(TargetConfig target, bool fromEarlierRun)
        {
        }

        public void TargetFinished(TargetSurvey target)
        {
        }

        public void RepoFinished(RepoSurvey repo)
        {
        }

        public void RunFinished(SurveyReport report, string reportPath)
        {
        }
    }
}
