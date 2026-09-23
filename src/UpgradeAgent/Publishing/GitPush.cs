using UpgradeAgent.Infrastructure;

namespace UpgradeAgent.Publishing;

/// <summary>Pushes with an auth header passed through git's environment-based config, never argv or .git/config.</summary>
public sealed class GitPush(IProcessRunner processRunner)
{
    public async Task<ProcessResult> PushAsync(string worktree, string remoteUrl, string branch, AzureDevOpsCredential credential, CancellationToken cancellationToken) =>
        await processRunner.RunAsync(
            "git",
            ["push", "--porcelain", CleanUrl(remoteUrl), $"HEAD:refs/heads/{branch}"],
            worktree,
            Environment(remoteUrl, credential.AuthorizationHeader),
            cancellationToken);

    /// <summary>
    /// GIT_CONFIG_COUNT/KEY/VALUE inject config for this process only. The header is scoped to the
    /// remote's host, and the credential helper is cleared so Git Credential Manager can't pop up on stage.
    /// </summary>
    internal static Dictionary<string, string?> Environment(string remoteUrl, string authorization)
    {
        var uri = new Uri(remoteUrl);
        return new Dictionary<string, string?>
        {
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GCM_INTERACTIVE"] = "never",
            ["GIT_CONFIG_COUNT"] = "2",
            ["GIT_CONFIG_KEY_0"] = $"http.{uri.Scheme}://{uri.Host}/.extraheader",
            ["GIT_CONFIG_VALUE_0"] = $"AUTHORIZATION: {authorization}",
            ["GIT_CONFIG_KEY_1"] = "credential.helper",
            ["GIT_CONFIG_VALUE_1"] = "",
        };
    }

    /// <summary>Azure DevOps returns remote URLs like https://org@dev.azure.com/...; a user name there triggers credential prompts.</summary>
    internal static string CleanUrl(string remoteUrl) =>
        new UriBuilder(remoteUrl) { UserName = "", Password = "" }.Uri.ToString();
}
