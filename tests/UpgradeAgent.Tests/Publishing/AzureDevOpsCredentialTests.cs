using System.Text;
using UpgradeAgent.Config;
using UpgradeAgent.Publishing;

namespace UpgradeAgent.Tests.Publishing;

public class AzureDevOpsCredentialTests
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

    [Theory]
    [InlineData("pat", "token", "secret", "PAT from $ADO_PAT")]
    [InlineData(null, "token", "secret", "token from $SYSTEM_ACCESSTOKEN")]
    [InlineData(null, null, "secret", "PAT from user secrets")]
    public async Task EnvironmentVariablesComeBeforeUserSecrets(string? pat, string? token, string? secret, string expectedSource)
    {
        var environment = new Dictionary<string, string?> { ["ADO_PAT"] = pat, ["SYSTEM_ACCESSTOKEN"] = token };
        var provider = new AzureDevOpsCredentialProvider(new AzureDevOpsOptions { Pat = secret, UseAzureIdentity = false }, name => environment.GetValueOrDefault(name));

        Assert.Equal(expectedSource, (await provider.AcquireAsync(CancellationToken.None)).Source);
    }

    [Fact]
    public async Task NoCredentialIsAConfigurationErrorThatSaysWhatToDo()
    {
        var provider = new AzureDevOpsCredentialProvider(new AzureDevOpsOptions { UseAzureIdentity = false }, _ => null);

        var error = await Assert.ThrowsAsync<ConfigurationException>(() => provider.AcquireAsync(CancellationToken.None));

        Assert.Contains("dotnet user-secrets set AzureDevOps:Pat", error.Message, StringComparison.Ordinal);
    }
}
