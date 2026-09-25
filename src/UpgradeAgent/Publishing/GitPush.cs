using UpgradeAgent.Infrastructure;

namespace UpgradeAgent.Publishing;

internal sealed class GitPush(IProcessRunner processRunner)
{
    public async Task<ProcessResult> PushAsync(string worktree, string remoteUrl, string branch, AzureDevOpsCredential credential, CancellationToken cancellationToken) =>
        await processRunner.RunAsync(
            "git",
            ["push", "--porcelain", CleanUrl(remoteUrl), $"HEAD:refs/heads/{branch}"],
            worktree,
            Environment(remoteUrl, credential.AuthorizationHeader),
            cancellationToken);

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

    internal static string CleanUrl(string remoteUrl) =>
        new UriBuilder(remoteUrl) { UserName = "", Password = "" }.Uri.ToString();
}
