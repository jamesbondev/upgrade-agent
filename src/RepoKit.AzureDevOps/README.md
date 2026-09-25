# RepoKit.AzureDevOps

A small .NET 10 library for Azure DevOps credentials and repo addresses. Its only package is `Azure.Identity`, and it
doesn't reference RepoKit, so you can use it with any git or HTTP code. The csproj builds with or without central
package management.

```csharp
using RepoKit.AzureDevOps;

var credentials = new AzureDevOpsCredentialProvider(new AzureDevOpsAuthOptions());
var credential = await credentials.AcquireAsync(cancellationToken);

var repo = new AzureDevOpsRepo("https://dev.azure.com/contoso", "Team Project", "payments-api");
// repo.CloneUrl: https://dev.azure.com/contoso/Team%20Project/_git/payments-api
// credential.AuthorizationHeader: "Basic ..." or "Bearer ...", for git (RepoKit's RepoSource.Remote) or REST calls
```

## Where the credential comes from

`AcquireAsync` uses the first of these that is set:

1. A PAT in the environment variable named by `PatEnvVar` (default `ADO_PAT`).
2. A token in `AccessTokenEnvVar` (default `SYSTEM_ACCESSTOKEN`, which Azure Pipelines provides).
3. `Pat`, from your app's configuration (for example user secrets).
4. With `UseAzureIdentity` (the default), `DefaultAzureCredential`: `az login`, a managed identity, and so on.

If none works, it throws `AzureDevOpsAuthException` with a message that says what to set. `ToString()` on a
credential names its source, never the secret.

When an agent runs commands in your app, hide the credential variables from it:
`credentials.SecretEnvironmentVariables` lists them (for AgentHarness, add them to
`CopilotOptions.HiddenEnvironmentVariables`).
