using System.Collections;

namespace UpgradeAgent.Agent;

/// <summary>
/// The environment for the agent's runtime (and so every command it runs). The Copilot runtime uses
/// exactly this dictionary (verified: it replaces the inherited environment), so dropping a name here
/// really hides it.
/// </summary>
public static class AgentEnvironment
{
    private static readonly string[] SecretNames =
    [
        "SYSTEM_ACCESSTOKEN", "ADO_PAT", "AZURE_DEVOPS_EXT_PAT", "VSS_NUGET_ACCESSTOKEN", "VSS_NUGET_EXTERNAL_FEED_ENDPOINTS",
        "NUGET_API_KEY", "GH_TOKEN", "GITHUB_TOKEN", "COPILOT_GITHUB_TOKEN", "OPENAI_API_KEY", "ANTHROPIC_API_KEY",
    ];

    private static readonly string[] SecretPrefixes = ["AZURE_", "ARM_", "AWS_"];

    private static readonly string[] SecretFragments = ["_PAT", "SECRET", "PASSWORD", "PASSWD", "APIKEY", "API_KEY", "CONNECTIONSTRING", "CONNECTION_STRING", "ACCESS_TOKEN", "ACCESSTOKEN"];

    public static Dictionary<string, string> Build(IDictionary current, IEnumerable<string> extraNamesToRemove, IReadOnlyDictionary<string, string?> overrides)
    {
        var extra = extraNamesToRemove.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var environment = current.Cast<DictionaryEntry>()
            .Select(e => (Name: (string)e.Key, Value: e.Value as string ?? ""))
            .Where(e => !IsSecret(e.Name) && !extra.Contains(e.Name))
            .ToDictionary(e => e.Name, e => e.Value, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach (var (name, value) in overrides)
        {
            if (value is null)
            {
                environment.Remove(name);
            }
            else
            {
                environment[name] = value;
            }
        }

        return environment;
    }

    public static bool IsSecret(string name)
    {
        var upper = name.ToUpperInvariant();
        return SecretNames.Contains(upper, StringComparer.Ordinal)
            || SecretPrefixes.Any(p => upper.StartsWith(p, StringComparison.Ordinal))
            || SecretFragments.Any(f => upper.Contains(f, StringComparison.Ordinal));
    }
}
