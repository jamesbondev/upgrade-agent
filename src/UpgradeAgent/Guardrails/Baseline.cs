using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UpgradeAgent.Build;

namespace UpgradeAgent.Guardrails;

/// <summary>Green-before-we-start evidence, cached per (commit, SDK, test args) so rehearsals skip it.</summary>
internal sealed record Baseline(
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

/// <summary>Baselines on disk, one file per (commit, SDK, test arguments).</summary>
internal sealed class BaselineCache(string cacheDirectory)
{
    public Baseline? TryLoad(string commit, string sdkVersion, IReadOnlyList<string> testArguments)
    {
        var path = PathFor(commit, sdkVersion, testArguments);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<Baseline>(File.ReadAllText(path), JsonDefaults.Options) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Save(Baseline baseline, IReadOnlyList<string> testArguments)
    {
        var path = PathFor(baseline.Commit, baseline.SdkVersion, testArguments);
        Directory.CreateDirectory(cacheDirectory);
        File.WriteAllText(path, JsonSerializer.Serialize(baseline, JsonDefaults.Options));
    }

    private string PathFor(string commit, string sdkVersion, IReadOnlyList<string> testArguments)
    {
        var argumentsHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', testArguments))))[..8];
        return Path.Combine(cacheDirectory, $"{commit[..12]}-{sdkVersion}-{argumentsHash}.json");
    }
}
