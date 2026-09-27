using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using RepoKit;
using TestHardener.Config;
using TestHardener.Infrastructure;
using TestHardener.Stryker;
using TestHardener.Tests.TestSupport;

namespace TestHardener.Tests.Stryker;

public class StrykerConfigTests
{
    private static readonly TargetConfig Target = new("Demo", "src/Demo/Demo.csproj", ["tests/Demo.Tests/Demo.Tests.csproj"], null, [], []);

    [Fact]
    public void Build_WritesProjectModeSettings()
    {
        var json = Config(new StrykerRequest("/clone", Target, TestRunnerMode.VSTest, ["!**/Migrations/**"], "FullyQualifiedName~Add"), new StrykerOptions { Concurrency = 4 });

        Assert.Equal("Demo.csproj", (string?)json["project"]);
        Assert.Equal(Path.GetFullPath("/clone/tests/Demo.Tests/Demo.Tests.csproj"), (string?)json["test-projects"]![0]);
        Assert.Equal("vstest", (string?)json["test-runner"]);
        Assert.Equal("perTest", (string?)json["coverage-analysis"]);
        Assert.Equal("FullyQualifiedName~Add", (string?)json["test-case-filter"]);
        Assert.Equal("!**/Migrations/**", (string?)json["mutate"]![0]);
        Assert.Equal(4, (int?)json["concurrency"]);
        Assert.Equal(["json", "html"], json["reporters"]!.AsArray().Select(r => (string?)r));
        Assert.Contains("*Log*", json["ignore-methods"]!.AsArray().Select(r => (string?)r));
    }

    [Fact]
    public void Build_LeavesOutEmptySettings()
    {
        var json = Config(new StrykerRequest("/clone", Target, TestRunnerMode.VSTest, [], null), new StrykerOptions { IgnoreMethods = [] });

        Assert.Null(json["test-case-filter"]);
        Assert.Null(json["mutate"]);
        Assert.Null(json["ignore-methods"]);
        Assert.Null(json["concurrency"]);
    }

    [Fact]
    public void Build_TestingPlatform_UsesMtp()
    {
        var json = Config(new StrykerRequest("/clone", Target, TestRunnerMode.TestingPlatform, [], null), new StrykerOptions());

        Assert.Equal("mtp", (string?)json["test-runner"]);
    }

    [Fact]
    public void Build_FilterOnTestingPlatform_IsAConfigurationError() =>
        Assert.Throws<ConfigurationException>(() =>
            StrykerConfig.Build(new StrykerRequest("/clone", Target, TestRunnerMode.TestingPlatform, [], "Name=A"), new StrykerOptions()));

    private static JsonNode Config(StrykerRequest request, StrykerOptions options) =>
        JsonNode.Parse(StrykerConfig.Build(request, options))!["stryker-config"]!;
}

public class StrykerToolTests
{
    private const string Listed = """
        Package Id          Version      Commands
        ----------------------------------------------
        dotnet-stryker      5.0.0        dotnet-stryker
        """;

    [Fact]
    public void ParseVersion_ReadsTheToolList()
    {
        Assert.Equal("5.0.0", StrykerTool.ParseVersion(Listed));
        Assert.Null(StrykerTool.ParseVersion("Package Id   Version   Commands\n------"));
    }

    [Fact]
    public async Task EnsureAsync_RightVersion_DoesNotInstall()
    {
        using var tools = new TempDirectory();
        var runner = new FakeProcessRunner(_ => FakeProcessRunner.Ok(Listed));

        var path = await new StrykerTool(runner).EnsureAsync(tools.Path, "5.0.0", new Dictionary<string, string?>(), CancellationToken.None);

        Assert.Equal(StrykerTool.ExecutablePath(tools.Path), path);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task EnsureAsync_OtherVersion_ReinstallsThePinnedOne()
    {
        using var tools = new TempDirectory();
        var installed = "4.8.0";
        var runner = new FakeProcessRunner(call =>
        {
            if (call.Arguments[1] == "install")
            {
                installed = call.Arguments[4];
            }

            return FakeProcessRunner.Ok(call.Arguments[1] == "list" ? Listed.Replace("5.0.0", installed, StringComparison.Ordinal) : "");
        });

        await new StrykerTool(runner).EnsureAsync(tools.Path, "5.0.0", new Dictionary<string, string?>(), CancellationToken.None);

        Assert.Equal(["list", "uninstall", "install", "list"], runner.Calls.Select(c => c.Arguments[1]));
        Assert.Equal(["tool", "install", "dotnet-stryker", "--version", "5.0.0", "--tool-path", tools.Path], runner.Calls[2].Arguments);
    }

    [Fact]
    public async Task EnsureAsync_InstallFails_Throws()
    {
        using var tools = new TempDirectory();
        var runner = new FakeProcessRunner(call => call.Arguments[1] == "install" ? new ProcessResult(1, "", "no network") : FakeProcessRunner.Ok(""));

        var error = await Assert.ThrowsAsync<StrykerToolException>(() =>
            new StrykerTool(runner).EnsureAsync(tools.Path, "5.0.0", new Dictionary<string, string?>(), CancellationToken.None));
        Assert.Contains("no network", error.Message, StringComparison.Ordinal);
    }
}

public class StrykerRunnerTests
{
    private const string ToolList = "dotnet-stryker   5.0.0   dotnet-stryker";

    private static readonly TargetConfig Target = new("Demo", "src/Demo/Demo.csproj", ["tests/Demo.Tests/Demo.Tests.csproj"], null, [], []);

    [Fact]
    public async Task RunAsync_RunsFromTheProjectFolderWithTheConfigAndEnvironment()
    {
        using var repo = new TempDirectory().Write("src/Demo/Demo.csproj", "<Project />");
        using var output = new TempDirectory();
        var report = new ReportBuilder(repo.Path).Test("t1", "T.A").Mutant(Samples.CalculatorPath, Samples.Calculator, "a > b", MutantStatus.Survived).Json();
        var runner = Runner(report, 0);
        var environment = new Dictionary<string, string?> { ["ADO_PAT"] = null };

        var run = await Stryker(runner, output.Path).RunAsync(Request(repo.Path), output.Path, environment, CancellationToken.None);

        Assert.Null(run.Failure);
        Assert.Single(run.Report!.Mutants);
        var call = runner.Calls.Last();
        Assert.Equal(Path.Combine(repo.Path, "src", "Demo"), call.WorkingDirectory);
        Assert.Equal(["--config-file", Path.Combine(output.Path, "stryker-config.json"), "--output", output.Path], call.Arguments);
        Assert.Same(environment, call.Environment);
        Assert.True(File.Exists(Path.Combine(output.Path, "stryker.log")));
    }

    [Fact]
    public async Task RunAsync_ExitZeroWithoutAReport_Fails()
    {
        using var repo = new TempDirectory().Write("src/Demo/Demo.csproj", "<Project />");
        using var output = new TempDirectory();

        var run = await Stryker(Runner(null, 0), output.Path).RunAsync(Request(repo.Path), output.Path, new Dictionary<string, string?>(), CancellationToken.None);

        Assert.Null(run.Report);
        Assert.Contains("wrote no mutation report", run.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_NonZeroExit_Fails()
    {
        using var repo = new TempDirectory().Write("src/Demo/Demo.csproj", "<Project />");
        using var output = new TempDirectory();

        var run = await Stryker(Runner(null, 1), output.Path).RunAsync(Request(repo.Path), output.Path, new Dictionary<string, string?>(), CancellationToken.None);

        Assert.Contains("exited with code 1", run.Failure, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_MissingProjectFolder_Fails()
    {
        using var repo = new TempDirectory();
        using var output = new TempDirectory();

        var run = await Stryker(Runner(null, 0), output.Path).RunAsync(Request(repo.Path), output.Path, new Dictionary<string, string?>(), CancellationToken.None);

        Assert.Contains("is not in the repository", run.Failure, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, true, "has no mutants")]
    [InlineData(true, false, "lists no tests")]
    public async Task ReadAsync_ReportWithoutMutantsOrTests_Fails(bool withMutant, bool withTest, string expected)
    {
        using var output = new TempDirectory();
        var builder = new ReportBuilder("/r");
        if (withTest)
        {
            builder.Test("t1", "T.A");
        }

        if (withMutant)
        {
            builder.Mutant(Samples.CalculatorPath, Samples.Calculator, "a > b", MutantStatus.Killed);
        }

        output.Write(StrykerRunner.ReportRelativePath, builder.Json());

        var run = await StrykerRunner.ReadAsync(0, output.Path, "/r", TimeSpan.Zero, CancellationToken.None);

        Assert.Null(run.Report);
        Assert.Contains(expected, run.Failure, StringComparison.Ordinal);
    }

    private static StrykerRequest Request(string repoRoot) => new(repoRoot, Target, TestRunnerMode.VSTest, [], null);

    private static FakeProcessRunner Runner(string? report, int exitCode) => new(call =>
    {
        if (call.FileName == "dotnet")
        {
            return FakeProcessRunner.Ok(ToolList);
        }

        if (report is not null)
        {
            var output = call.Arguments[3];
            Directory.CreateDirectory(Path.Combine(output, "reports"));
            File.WriteAllText(Path.Combine(output, StrykerRunner.ReportRelativePath), report);
        }

        return new ProcessResult(exitCode, "stryker output", "");
    });

    private static StrykerRunner Stryker(FakeProcessRunner runner, string scratch)
    {
        var options = new TestHardenerOptions();
        var config = new ResolvedConfig(options, [], "/out", "/work", Path.Combine(scratch, "tools"));
        return new StrykerRunner(runner, config, new StrykerTool(runner), new FakeTimeProvider());
    }
}
