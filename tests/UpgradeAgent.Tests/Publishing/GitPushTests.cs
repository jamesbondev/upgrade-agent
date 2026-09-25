using UpgradeAgent.Publishing;

namespace UpgradeAgent.Tests.Publishing;

public class GitPushTests
{
    [Fact]
    public void GitGetsTheHeaderThroughScopedEnvironmentConfig()
    {
        var environment = GitPush.Environment("https://contoso@dev.azure.com/contoso/Demo/_git/LoanLedger", "Basic xyz");

        Assert.Equal("2", environment["GIT_CONFIG_COUNT"]);
        Assert.Equal("http.https://dev.azure.com/.extraheader", environment["GIT_CONFIG_KEY_0"]);
        Assert.Equal("AUTHORIZATION: Basic xyz", environment["GIT_CONFIG_VALUE_0"]);
        Assert.Equal(("credential.helper", ""), (environment["GIT_CONFIG_KEY_1"], environment["GIT_CONFIG_VALUE_1"]));
        Assert.Equal("0", environment["GIT_TERMINAL_PROMPT"]);
    }

    [Fact]
    public void RemoteUrlLosesItsUserName()
    {
        Assert.Equal("https://dev.azure.com/contoso/Demo/_git/LoanLedger", GitPush.CleanUrl("https://contoso@dev.azure.com/contoso/Demo/_git/LoanLedger"));
    }
}
