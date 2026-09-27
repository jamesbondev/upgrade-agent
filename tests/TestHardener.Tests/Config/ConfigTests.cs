using System.Collections;
using TestHardener.Config;
using TestHardener.Infrastructure;

namespace TestHardener.Tests.Config;

public class OptionsValidatorTests
{
    [Fact]
    public void Validate_CompleteConfig_Passes() =>
        Assert.True(new OptionsValidator().Validate(null, Valid()).Succeeded);

    [Fact]
    public void Validate_ReportsEachProblem()
    {
        var options = Valid();
        options.Repos[0].Solution = "";
        options.Repos[0].Targets[0].Project = "../escape/App.csproj";
        options.Repos[0].Targets[0].TestProjects = [];
        options.Stryker.MutationLevel = "Extreme";
        options.Stryker.Concurrency = 0;

        var failures = new OptionsValidator().Validate(null, options).Failures!.ToList();

        Assert.Contains(failures, f => f.Contains("no Solution", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.Contains("Project must be a repo-relative path", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.Contains("TestProjects must list", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.Contains("MutationLevel", StringComparison.Ordinal));
        Assert.Contains(failures, f => f.Contains("Concurrency", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_NoTargets_Fails()
    {
        var options = Valid();
        options.Repos[0].Targets.Clear();

        Assert.Contains(new OptionsValidator().Validate(null, options).Failures!, f => f.Contains("no Targets", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_DuplicateTargets_Fails()
    {
        var options = Valid();
        options.Repos[0].Targets.Add(new TargetOptions { Project = "other/App.csproj", TestProjects = ["tests/App.Tests.csproj"] });

        Assert.Contains(new OptionsValidator().Validate(null, options).Failures!, f => f.Contains("target App 2 times", StringComparison.Ordinal));
    }

    [Fact]
    public void Resolve_NormalisesPathsAndNamesTargets()
    {
        var config = ConfigLoader.Resolve(Valid(), "/configs");

        var target = config.Repos[0].Targets.Single();
        Assert.Equal("App", target.Name);
        Assert.Equal("src/App/App.csproj", target.Project);
        Assert.Equal(["tests/App.Tests/App.Tests.csproj"], target.TestProjects);
        Assert.Equal(Path.GetFullPath("/configs/demo"), config.Repos[0].LocalPath);
    }

    [Fact]
    public void ExpandHome_ReplacesTheTilde() =>
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache/tools"),
            ConfigLoader.ExpandHome("~/.cache/tools"));

    [Fact]
    public void EffectiveIgnoreMethods_DefaultsUntilSet()
    {
        Assert.Equal(StrykerOptions.DefaultIgnoreMethods, new StrykerOptions().EffectiveIgnoreMethods);
        Assert.Equal(["Only"], new StrykerOptions { IgnoreMethods = ["Only"] }.EffectiveIgnoreMethods);
    }

    private static TestHardenerOptions Valid() => new()
    {
        Repos =
        [
            new RepoOptions
            {
                Name = "demo",
                Path = "demo",
                Solution = "Demo.slnx",
                Targets = [new TargetOptions { Project = "src\\App\\App.csproj", TestProjects = ["tests/App.Tests/App.Tests.csproj"] }],
            },
        ],
    };
}

public class SafeEnvironmentTests
{
    [Fact]
    public void For_RemovesSecretsAndCredentialVariables()
    {
        var current = new Hashtable
        {
            ["ADO_PAT"] = "secret",
            ["MY_API_TOKEN"] = "secret",
            ["CUSTOM_CRED"] = "secret",
            ["PATH"] = "/usr/bin",
            ["HOME"] = "/home/me",
        };

        var environment = SafeEnvironment.For(current, ["CUSTOM_CRED"]);

        Assert.Null(environment["ADO_PAT"]);
        Assert.Null(environment["MY_API_TOKEN"]);
        Assert.Null(environment["CUSTOM_CRED"]);
        Assert.True(environment.ContainsKey("ADO_PAT"));
        Assert.False(environment.ContainsKey("PATH"));
        Assert.False(environment.ContainsKey("HOME"));
        Assert.Equal("en", environment["DOTNET_CLI_UI_LANGUAGE"]);
    }
}
