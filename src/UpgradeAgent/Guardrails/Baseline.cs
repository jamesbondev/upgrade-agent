using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UpgradeAgent.Build;

namespace UpgradeAgent.Guardrails;

/// <summary>Green-before-we-start evidence, cached per (commit, SDK, test args) so rehearsals skip it.</summary>
public sealed record Baseline(
    string Commit,
    string SdkVersion,
    TestRunnerMode RunnerMode,
    TestInventory? Tests,
    TestCounts? Counts,
    IReadOnlyList<string> TestFiles,
    DateTimeOffset CreatedUtc)
{
    public int PassedCount => Tests?.Passed ?? Counts?.Passed ?? 0;
}

public sealed class BaselineCache(string cacheDirectory)
{
    public string PathFor(string commit, string sdkVersion, IReadOnlyList<string> testArguments)
    {
        var argumentsHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', testArguments))))[..8];
        return Path.Combine(cacheDirectory, $"{commit[..12]}-{sdkVersion}-{argumentsHash}.json");
    }

    public Baseline? TryLoad(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<Baseline>(File.ReadAllText(path), JsonDefaults.Options) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(string path, Baseline baseline)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(baseline, JsonDefaults.Options));
    }
}
