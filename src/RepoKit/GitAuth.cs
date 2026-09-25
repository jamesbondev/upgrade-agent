namespace RepoKit;

public static class GitAuth
{
    public static IReadOnlyDictionary<string, string?> HeaderEnvironment(string remoteUrl, string authorizationHeader)
    {
        var uri = new Uri(remoteUrl);
        return new Dictionary<string, string?>
        {
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GCM_INTERACTIVE"] = "never",
            ["GIT_CONFIG_COUNT"] = "2",
            ["GIT_CONFIG_KEY_0"] = $"http.{uri.Scheme}://{uri.Host}/.extraheader",
            ["GIT_CONFIG_VALUE_0"] = $"AUTHORIZATION: {authorizationHeader}",
            ["GIT_CONFIG_KEY_1"] = "credential.helper",
            ["GIT_CONFIG_VALUE_1"] = "",
        };
    }

    public static string CleanUrl(string remoteUrl) =>
        Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri) && uri.UserInfo.Length > 0
            ? new UriBuilder(uri) { UserName = "", Password = "" }.Uri.ToString()
            : remoteUrl;
}
