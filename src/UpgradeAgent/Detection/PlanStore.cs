using System.Text.Json;
using UpgradeAgent.Infrastructure;
using UpgradeAgent.Run;

namespace UpgradeAgent.Detection;

internal static class PlanStore
{
    public static async Task SaveAsync(UpgradePlan plan, string path, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(plan, JsonDefaults.Options), cancellationToken);
    }

    public static UpgradePlan Load(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<UpgradePlan>(File.ReadAllText(path), JsonDefaults.Options)
                ?? throw new RunAbortedException($"Plan file {path} is empty.");
        }
        catch (JsonException ex)
        {
            throw new RunAbortedException($"Plan file {path} is not a valid plan: {ex.Message}");
        }
    }
}

internal abstract record PlanSource
{
    public sealed record Detect : PlanSource;

    public sealed record FromFile(string Path) : PlanSource;

    public sealed record Frozen(UpgradePlan Plan) : PlanSource;
}
