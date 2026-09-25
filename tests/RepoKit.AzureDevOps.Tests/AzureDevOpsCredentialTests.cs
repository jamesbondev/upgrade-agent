using System.Text;

namespace RepoKit.AzureDevOps.Tests;

public class AzureDevOpsCredentialTests
{
    [Fact]
    public void PatBecomesABasicHeaderWithAnEmptyUserName()
    {
        var credential = AzureDevOpsCredential.FromPat("secret-pat", "test");

        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(":secret-pat")), credential.AuthorizationHeader);
        Assert.DoesNotContain("secret-pat", credential.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(credential.AuthorizationHeader, credential.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TokenBecomesABearerHeader()
    {
        Assert.Equal("Bearer abc", AzureDevOpsCredential.FromBearer("abc", "test").AuthorizationHeader);
    }

    [Theory]
    [InlineData("pat", "token", "secret", "PAT from $ADO_PAT")]
    [InlineData(null, "token", "secret", "token from $SYSTEM_ACCESSTOKEN")]
    [InlineData(null, null, "secret", "PAT from configuration")]
    public async Task EnvironmentVariablesComeBeforeConfiguration(string? pat, string? token, string? secret, string expectedSource)
    {
        var environment = new Dictionary<string, string?> { ["ADO_PAT"] = pat, ["SYSTEM_ACCESSTOKEN"] = token };
        var provider = new AzureDevOpsCredentialProvider(new AzureDevOpsAuthOptions { Pat = secret, UseAzureIdentity = false }, name => environment.GetValueOrDefault(name));

        Assert.Equal(expectedSource, (await provider.AcquireAsync(CancellationToken.None)).Source);
    }

    [Fact]
    public async Task NoCredentialIsAnAuthErrorThatSaysWhatToDo()
    {
        var provider = new AzureDevOpsCredentialProvider(new AzureDevOpsAuthOptions { UseAzureIdentity = false }, _ => null);

        var error = await Assert.ThrowsAsync<AzureDevOpsAuthException>(() => provider.AcquireAsync(CancellationToken.None));

        Assert.Contains("$ADO_PAT", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("az login", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSecretVariablesFollowTheOptions()
    {
        var provider = new AzureDevOpsCredentialProvider(new AzureDevOpsAuthOptions { PatEnvVar = "MY_PAT", AccessTokenEnvVar = "MY_TOKEN" });

        Assert.Equal(["MY_PAT", "MY_TOKEN"], provider.SecretEnvironmentVariables);
    }
}
