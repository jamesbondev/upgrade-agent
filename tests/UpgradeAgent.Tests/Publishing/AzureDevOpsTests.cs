using System.Text;
using UpgradeAgent.Publishing;

namespace UpgradeAgent.Tests.Publishing;

public class AzureDevOpsTests
{
    [Fact]
    public void PatBecomesABasicHeaderWithAnEmptyUserName()
    {
        var credential = AzureDevOpsCredential.FromPat("secret-pat", "test");

        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(":secret-pat")), credential.AuthorizationHeader);
        Assert.DoesNotContain("secret-pat", credential.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TokenBecomesABearerHeader()
    {
        Assert.Equal("Bearer abc", AzureDevOpsCredential.FromBearer("abc", "test").AuthorizationHeader);
    }

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

    [Fact]
    public void ShortDescriptionsAreUnchanged()
    {
        Assert.Equal("# Title\nbody", AzureDevOpsClient.FitDescription("# Title\nbody"));
    }

    [Fact]
    public void LongDescriptionsDropDetailsFirst()
    {
        var markdown = "# Title\n" + $"<details><summary>x</summary>\n{new string('d', 5000)}\n</details>\n" + "## Run\n- ok\n";

        var fitted = AzureDevOpsClient.FitDescription(markdown);

        Assert.Equal("# Title\n## Run\n- ok\n", fitted);
    }

    [Fact]
    public void VeryLongDescriptionsAreCutAtALineWithANotice()
    {
        var markdown = string.Join('\n', Enumerable.Range(0, 400).Select(i => $"- line {i} with some text"));

        var fitted = AzureDevOpsClient.FitDescription(markdown);

        Assert.True(fitted.Length <= AzureDevOpsClient.MaxDescriptionLength);
        Assert.EndsWith("The full report is the first comment._", fitted, StringComparison.Ordinal);
        Assert.Contains("- line 1 with some text\n", fitted, StringComparison.Ordinal);
        Assert.DoesNotContain("- line 399", fitted, StringComparison.Ordinal);
    }
}
