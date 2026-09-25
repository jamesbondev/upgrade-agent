namespace RepoKit.AzureDevOps.Tests;

public class AzureDevOpsRepoTests
{
    [Theory]
    [InlineData("https://dev.azure.com/contoso", "Team", "payments-api", "https://dev.azure.com/contoso/Team/_git/payments-api")]
    [InlineData("https://dev.azure.com/contoso/", "Team Project", "My Repo", "https://dev.azure.com/contoso/Team%20Project/_git/My%20Repo")]
    [InlineData("https://contoso.visualstudio.com", "Team", "api", "https://contoso.visualstudio.com/Team/_git/api")]
    public void CloneUrlIsBuiltFromTheParts(string organization, string project, string repository, string expected)
    {
        Assert.Equal(expected, new AzureDevOpsRepo(organization, project, repository).CloneUrl);
    }

    [Theory]
    [InlineData("dev.azure.com/contoso")]
    [InlineData("http://dev.azure.com/contoso")]
    [InlineData("")]
    public void TheOrganizationMustBeAnHttpsUrl(string organization)
    {
        Assert.Throws<ArgumentException>(() => new AzureDevOpsRepo(organization, "Team", "api"));
    }

    [Fact]
    public void CredentialsInTheOrganizationUrlAreDropped()
    {
        Assert.Equal("https://dev.azure.com/contoso", new AzureDevOpsRepo("https://user:pat@dev.azure.com/contoso", "Team", "api").OrganizationUrl);
    }
}
