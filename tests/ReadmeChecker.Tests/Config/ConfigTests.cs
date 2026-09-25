using ReadmeChecker.Config;

namespace ReadmeChecker.Tests.Config;

public class ConfigTests
{
    [Fact]
    public void AzureDevOpsReposUseTheDefaultsUnlessTheyOverrideThem()
    {
        var options = new ReadmeCheckerOptions
        {
            AzureDevOps = new AzureDevOpsSettings { OrganizationUrl = "https://dev.azure.com/contoso", Project = "Team" },
            Repos =
            [
                new RepoOptions { Name = "payments-api" },
                new RepoOptions { Name = "docs-site", Project = "Other Team", Readme = "\\docs\\README.md" },
            ],
        };

        var config = ConfigLoader.Resolve(options, "/base");

        Assert.Equal("https://dev.azure.com/contoso/Team/_git/payments-api", config.Repos[0].AzureDevOps?.CloneUrl);
        Assert.Equal("https://dev.azure.com/contoso/Other%20Team/_git/docs-site", config.Repos[1].AzureDevOps?.CloneUrl);
        Assert.Equal("docs/README.md", config.Repos[1].ReadmePath);
    }

    [Fact]
    public void ALocalRepoPathIsRelativeToTheConfigFile()
    {
        var options = new ReadmeCheckerOptions { Repos = [new RepoOptions { Name = "demo", Path = "../demo" }] };

        var config = ConfigLoader.Resolve(options, Path.Combine(Path.GetTempPath(), "configs"));

        Assert.Equal(Path.Combine(Path.GetTempPath(), "demo"), config.Repos[0].LocalPath);
        Assert.Null(config.Repos[0].AzureDevOps);
    }

    [Fact]
    public void ABadOrganizationUrlIsAConfigurationError()
    {
        var options = new ReadmeCheckerOptions
        {
            AzureDevOps = new AzureDevOpsSettings { OrganizationUrl = "dev.azure.com/contoso", Project = "Team" },
            Repos = [new RepoOptions { Name = "api" }],
        };

        var error = Assert.Throws<ConfigurationException>(() => ConfigLoader.Resolve(options, "/base"));

        Assert.StartsWith("Repo 'api':", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheValidatorExplainsEveryProblem()
    {
        var options = new ReadmeCheckerOptions
        {
            Repos = [new RepoOptions { Name = "api" }, new RepoOptions { Name = "API", Path = "x" }, new RepoOptions()],
            Readme = new ReadmeOptions { MaxCandidates = 0 },
        };

        var result = new OptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, f => f.Contains("no AzureDevOps:OrganizationUrl", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, f => f.Contains("listed 2 times", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, f => f.Contains("Repos[2] has no Name", StringComparison.Ordinal));
        Assert.Contains(result.Failures!, f => f.Contains("Readme:MaxCandidates", StringComparison.Ordinal));
    }

    [Fact]
    public void NoReposIsAProblem()
    {
        Assert.True(new OptionsValidator().Validate(null, new ReadmeCheckerOptions()).Failed);
    }
}
