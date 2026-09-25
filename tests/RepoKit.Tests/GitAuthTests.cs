namespace RepoKit.Tests;

public class GitAuthTests
{
    [Fact]
    public void GitGetsTheHeaderThroughScopedEnvironmentConfig()
    {
        var environment = GitAuth.HeaderEnvironment("https://dev.azure.com/contoso/Demo/_git/LoanLedger", "Basic xyz");

        Assert.Equal("2", environment["GIT_CONFIG_COUNT"]);
        Assert.Equal("http.https://dev.azure.com/.extraheader", environment["GIT_CONFIG_KEY_0"]);
        Assert.Equal("AUTHORIZATION: Basic xyz", environment["GIT_CONFIG_VALUE_0"]);
        Assert.Equal(("credential.helper", ""), (environment["GIT_CONFIG_KEY_1"], environment["GIT_CONFIG_VALUE_1"]));
        Assert.Equal("0", environment["GIT_TERMINAL_PROMPT"]);
    }

    [Theory]
    [InlineData("https://contoso@dev.azure.com/contoso/Demo/_git/LoanLedger", "https://dev.azure.com/contoso/Demo/_git/LoanLedger")]
    [InlineData("https://dev.azure.com/contoso/Demo/_git/LoanLedger", "https://dev.azure.com/contoso/Demo/_git/LoanLedger")]
    [InlineData("../relative/path", "../relative/path")]
    public void CleanUrlDropsUserInfoOnly(string url, string expected)
    {
        Assert.Equal(expected, GitAuth.CleanUrl(url));
    }

    [Fact]
    public void ARemoteSourceRefusesCredentialsInTheUrl()
    {
        Assert.Throws<ArgumentException>(() => RepoSource.Remote("demo", "https://user:pat@dev.azure.com/contoso/Demo/_git/Api"));
    }

    [Fact]
    public void ARemoteSourceNeverPrintsItsHeader()
    {
        var source = RepoSource.Remote("demo", "https://dev.azure.com/contoso/Demo/_git/Api", "Basic c2VjcmV0");

        Assert.DoesNotContain("c2VjcmV0", source.ToString(), StringComparison.Ordinal);
    }
}
