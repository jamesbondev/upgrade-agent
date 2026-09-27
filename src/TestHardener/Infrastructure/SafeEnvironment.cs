using System.Collections;
using AgentHarness;

namespace TestHardener.Infrastructure;

internal static class SafeEnvironment
{
    public static readonly IReadOnlyDictionary<string, string?> Dotnet = new Dictionary<string, string?>
    {
        ["DOTNET_CLI_UI_LANGUAGE"] = "en",
        ["DOTNET_NOLOGO"] = "1",
        ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
        ["MSBUILDDISABLENODEREUSE"] = "1",
        ["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0",
    };

    public static Dictionary<string, string?> For(IDictionary current, IEnumerable<string> credentialVariables)
    {
        var environment = new Dictionary<string, string?>(Dotnet, StringComparer.Ordinal);
        foreach (var name in current.Keys.Cast<string>().Where(AgentEnvironment.IsSecret).Concat(credentialVariables))
        {
            environment[name] = null;
        }

        return environment;
    }

    public static Dictionary<string, string?> ForCurrentProcess(IEnumerable<string> credentialVariables) =>
        For(Environment.GetEnvironmentVariables(), credentialVariables);
}
