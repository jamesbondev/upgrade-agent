using System.Text;
using Azure.Core;
using Azure.Identity;
using Microsoft.VisualStudio.Services.Common;
using Microsoft.VisualStudio.Services.OAuth;
using UpgradeAgent.Config;

namespace UpgradeAgent.Publishing;

/// <summary>
/// One credential in two forms: an HTTP Authorization value for git and VssCredentials for the REST client.
/// It lives only in this process: it's never written to .git/config, a remote URL, a command line or the
/// agent's environment.
/// </summary>
internal sealed record AzureDevOpsCredential(string Source, string AuthorizationHeader, VssCredentials VssCredentials)
{
    public override string ToString() => $"AzureDevOpsCredential({Source})";

    public static AzureDevOpsCredential FromPat(string pat, string source) =>
        new(source, "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + pat)), new VssBasicCredential(string.Empty, pat));

    public static AzureDevOpsCredential FromBearer(string token, string source) =>
        new(source, "Bearer " + token, new VssOAuthAccessTokenCredential(token));
}

/// <summary>Finds a credential: PAT env var, then bearer-token env var, then a PAT from user secrets, then Azure identity.</summary>
/// <param name="getEnvironmentVariable">Environment lookup (a seam: precedence is testable without touching the process environment).</param>
/// <param name="identity">Entra credential for the last step; null means <see cref="DefaultAzureCredential"/>.</param>
internal sealed class AzureDevOpsCredentialProvider(AzureDevOpsOptions options, Func<string, string?> getEnvironmentVariable, TokenCredential? identity = null)
{
    /// <summary>Azure DevOps' Entra resource ID.</summary>
    private const string Scope = "499b84ac-1321-427f-aa17-267ca6975798/.default";

    public AzureDevOpsCredentialProvider(AzureDevOpsOptions options)
        : this(options, Environment.GetEnvironmentVariable)
    {
    }

    private string Guidance =>
        $"No Azure DevOps credential: set ${options.PatEnvVar}, run 'dotnet user-secrets set AzureDevOps:Pat <pat> --project src/UpgradeAgent'" +
        (options.UseAzureIdentity ? ", or 'az login'." : ".");

    public async Task<AzureDevOpsCredential> AcquireAsync(CancellationToken cancellationToken)
    {
        if (getEnvironmentVariable(options.PatEnvVar) is { Length: > 0 } pat)
        {
            return AzureDevOpsCredential.FromPat(pat, $"PAT from ${options.PatEnvVar}");
        }

        if (getEnvironmentVariable(options.AccessTokenEnvVar) is { Length: > 0 } token)
        {
            return AzureDevOpsCredential.FromBearer(token, $"token from ${options.AccessTokenEnvVar}");
        }

        if (options.Pat is { Length: > 0 } secretPat)
        {
            return AzureDevOpsCredential.FromPat(secretPat, "PAT from user secrets");
        }

        if (!options.UseAzureIdentity)
        {
            throw new ConfigurationException(Guidance);
        }

        try
        {
            var accessToken = await (identity ?? new DefaultAzureCredential()).GetTokenAsync(new TokenRequestContext([Scope]), cancellationToken);
            return AzureDevOpsCredential.FromBearer(accessToken.Token, "Azure identity");
        }
        catch (AuthenticationFailedException ex)
        {
            throw new ConfigurationException($"{Guidance} ({ex.Message.Split('\n')[0]})");
        }
    }
}
