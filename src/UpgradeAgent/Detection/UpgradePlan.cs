using System.Text.Json.Serialization;
using NuGet.Frameworks;
using NuGet.Versioning;
using UpgradeAgent.Infrastructure;

namespace UpgradeAgent.Detection;

internal enum UpdateDecision
{
    Planned,
    Skipped,
    Manual,
    NeedsTfmUpgrade,
}

internal static class UpdateDecisionLabels
{
    public static string Label(this UpdateDecision decision) => decision switch
    {
        UpdateDecision.Planned => "planned",
        UpdateDecision.Manual => "manual",
        UpdateDecision.NeedsTfmUpgrade => "needs TFM upgrade",
        _ => "skipped",
    };
}

internal enum GroupKind
{
    PatchMinor,
    Major,
}

internal sealed record ProjectTarget(string ProjectPath, NuGetFramework Framework);

internal sealed record PlannedUpdate(
    string Id,
    NuGetVersion From,
    NuGetVersion To,
    BumpKind Kind,
    IReadOnlyList<ProjectTarget> Projects,
    UpdateDecision Decision,
    string? Reason,
    string? Group,
    string? Note = null);

internal sealed record UpdateGroup(string Name, GroupKind Kind, IReadOnlyList<PlannedUpdate> Updates);

internal sealed record UpgradePlan(DateTimeOffset CreatedUtc, string SolutionPath, IReadOnlyList<PlannedUpdate> Updates)
{
    public const string PatchMinorGroupName = "patch-minor";

    public const string NotSelectedReason = "not selected by --only";

    [JsonIgnore]
    public IReadOnlyList<UpdateGroup> Groups => Updates
        .Where(u => u is { Decision: UpdateDecision.Planned, Group: not null })
        .GroupBy(u => u.Group!, StringComparer.OrdinalIgnoreCase)
        .Select(g => new UpdateGroup(
            g.Key,
            g.Any(u => u.Kind == BumpKind.Major) ? GroupKind.Major : GroupKind.PatchMinor,
            g.OrderBy(u => u.Id, StringComparer.OrdinalIgnoreCase).ToList()))
        .OrderBy(g => g.Kind)
        .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public UpgradePlan Narrow(IReadOnlyCollection<string> only, PackageFamilies families)
    {
        if (only.Count == 0)
        {
            return this;
        }

        bool Selected(PlannedUpdate update) =>
            Glob.IsMatchAny(only, update.Id)
            || (families.Find(update.Id) is { } family && only.Contains(family, StringComparer.OrdinalIgnoreCase));

        return this with
        {
            Updates = Updates
                .Select(u => u.Decision != UpdateDecision.Skipped && !Selected(u) ? u with { Decision = UpdateDecision.Skipped, Reason = NotSelectedReason, Group = null } : u)
                .ToList(),
        };
    }
}
