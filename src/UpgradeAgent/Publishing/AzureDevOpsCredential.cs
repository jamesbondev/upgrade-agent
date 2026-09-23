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
public sealed record AzureDevOpsCredential(string Source, string AuthorizationHeader, VssCredentials VssCredentials)
{
    /// <summary>Azure DevOps' Entra resource ID.</summary>
    private const string Scope = "499b84ac-1321-427f-aa17-267ca6975798/.default";

    public override string ToString() => $"AzureDevOpsCredential({Source})";

    /// <summary>PAT env var, then bearer-token env var, then a PAT from user secrets, then Azure identity.</summary>
    public static async Task<AzureDevOpsCredential> AcquireAsync(AzureDevOpsOptions options, CancellationToken cancellationToken)
    {
        if (Environment.GetEnvironmentVariable(options.PatEnvVar) is { Length: > 0 } pat)
        {
            return FromPat(pat, $"PAT from ${options.PatEnvVar}");
        }

        if (Environment.GetEnvironmentVariable(options.AccessTokenEnvVar) is { Length: > 0 } token)
        {
            return FromBearer(token, $"token from ${options.AccessTokenEnvVar}");
        }

        if (options.Pat is { Length: > 0 } secretPat)
        {
            return FromPat(secretPat, "PAT from user secrets");
        }

        if (options.UseAzureIdentity)
        {
            try
            {
                var accessToken = await new DefaultAzureCredential().GetTokenAsync(new TokenRequestContext([Scope]), cancellationToken);
                return FromBearer(accessToken.Token, "Azure identity");
            }
            catch (AuthenticationFailedException ex)
            {
                throw new ConfigurationException($"No Azure DevOps credential: set ${options.PatEnvVar}, run 'dotnet user-secrets set AzureDevOps:Pat <pat>', or 'az login'. ({ex.Message.Split('\n')[0]})");
            }
        }

        throw new ConfigurationException($"No Azure DevOps credential: set ${options.PatEnvVar} or run 'dotnet user-secrets set AzureDevOps:Pat <pat> --project src/UpgradeAgent'.");
    }

    public static AzureDevOpsCredential FromPat(string pat, string source) =>
        new(source, "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + pat)), new VssBasicCredential(string.Empty, pat));

    public static AzureDevOpsCredential FromBearer(string token, string source) =>
        new(source, "Bearer " + token, new VssOAuthAccessTokenCredential(token));
}
