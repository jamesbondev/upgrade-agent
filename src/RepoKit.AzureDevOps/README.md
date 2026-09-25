# RepoKit.AzureDevOps

A small .NET 10 library for Azure DevOps credentials, repo addresses and draft pull requests. Its only package is `Azure.Identity`, and it
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

## Pull requests

`AzureDevOpsPullRequests` calls the Azure DevOps REST API (7.1) with a plain `HttpClient`, so it needs no Azure
DevOps SDK packages.

```csharp
var pullRequests = new AzureDevOpsPullRequests(httpClient, credential);

var previous = await pullRequests.ListAsync(repo, "agent/readme-refresh-", cancellationToken);
var created = await pullRequests.CreateDraftAsync(repo, "agent/readme-refresh-20260925-1200", "main",
    "Update the README", description, ["agent-generated"], cancellationToken);
// created.WebUrl: https://dev.azure.com/contoso/Team%20Project/_git/payments-api/pullrequest/42
```

- `ListAsync` returns pull requests in any state (`active`, `completed`, `abandoned`) whose source branch starts with
  the prefix, newest pages first, up to 2,000.
- `CreateDraftAsync` opens a draft with the labels you give. A description over Azure DevOps's 4,000-character limit
  is cut at a line break with a notice, and the full text is posted as the first (closed) comment.
- A failure throws `AzureDevOpsApiException` with the HTTP status and Azure DevOps's message. A response that isn't
  JSON, usually a sign-in page, means the credential isn't valid for that organization.
