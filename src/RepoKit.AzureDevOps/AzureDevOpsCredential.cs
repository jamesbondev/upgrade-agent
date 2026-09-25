using System.Text;
using Azure.Core;
using Azure.Identity;

namespace RepoKit.AzureDevOps;

public sealed class AzureDevOpsAuthException(string message) : Exception(message);

public sealed class AzureDevOpsAuthOptions
{
    public string PatEnvVar { get; set; } = "ADO_PAT";

    public string AccessTokenEnvVar { get; set; } = "SYSTEM_ACCESSTOKEN";

    public string? Pat { get; set; }

    public bool UseAzureIdentity { get; set; } = true;
}

public sealed record AzureDevOpsCredential(string Source, string AuthorizationHeader)
{
    public override string ToString() => $"AzureDevOpsCredential({Source})";

    public static AzureDevOpsCredential FromPat(string pat, string source) =>
        new(source, "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes(":" + pat)));

    public static AzureDevOpsCredential FromBearer(string token, string source) =>
        new(source, "Bearer " + token);
}

public sealed class AzureDevOpsCredentialProvider(AzureDevOpsAuthOptions options, Func<string, string?> getEnvironmentVariable, TokenCredential? identity = null)
{
    private const string Scope = "499b84ac-1321-427f-aa17-267ca6975798/.default";

    public AzureDevOpsCredentialProvider(AzureDevOpsAuthOptions options)
        : this(options, Environment.GetEnvironmentVariable)
    {
    }

    public IReadOnlyList<string> SecretEnvironmentVariables => [options.PatEnvVar, options.AccessTokenEnvVar];

    private string Guidance =>
        $"No Azure DevOps credential: set ${options.PatEnvVar} to a PAT, set AzureDevOps:Pat in your user secrets" +
        (options.UseAzureIdentity ? ", or run 'az login'." : ".");

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
            return AzureDevOpsCredential.FromPat(secretPat, "PAT from configuration");
        }

        if (!options.UseAzureIdentity)
        {
            throw new AzureDevOpsAuthException(Guidance);
        }

        try
        {
            var accessToken = await (identity ?? new DefaultAzureCredential()).GetTokenAsync(new TokenRequestContext([Scope]), cancellationToken);
            return AzureDevOpsCredential.FromBearer(accessToken.Token, "Azure identity");
        }
        catch (AuthenticationFailedException ex)
        {
            throw new AzureDevOpsAuthException($"{Guidance} ({ex.Message.Split('\n')[0]})");
        }
    }
}
