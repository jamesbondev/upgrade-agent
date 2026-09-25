using System.Text.Json;
using UpgradeAgent.Infrastructure;
using UpgradeAgent.Run;

namespace UpgradeAgent.Detection;

/// <summary>Reads and writes plan.json.</summary>
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

/// <summary>Where a run's plan comes from.</summary>
internal abstract record PlanSource
{
    /// <summary>Detect outdated packages now, in the run's worktree.</summary>
    public sealed record Detect : PlanSource;

    /// <summary>A plan saved earlier with <c>plan --plan-out</c>.</summary>
    public sealed record FromFile(string Path) : PlanSource;

    /// <summary>The plan a recording was made with (replay).</summary>
    public sealed record Frozen(UpgradePlan Plan) : PlanSource;
}
