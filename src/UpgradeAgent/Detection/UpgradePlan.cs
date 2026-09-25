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
    /// <summary>The words for a decision, shared by the console and the PR description.</summary>
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

/// <param name="ProjectPath">Relative to the repo root, with forward slashes.</param>
internal sealed record ProjectTarget(string ProjectPath, NuGetFramework Framework);

/// <summary>
/// One version step for one package. A package with a newer minor and a newer major produces two
/// steps: current → latest minor (in the patch/minor group), then latest minor → major.
/// </summary>
/// <param name="Reason">Why the update isn't planned; null when it is.</param>
/// <param name="Note">Something worth knowing about a planned update (e.g. compatibility couldn't be verified).</param>
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

/// <param name="Kind">Major when any of its updates is a major step.</param>
internal sealed record UpdateGroup(string Name, GroupKind Kind, IReadOnlyList<PlannedUpdate> Updates);

/// <summary>
/// The updates the policy decided on. Groups are derived from the planned updates, so a saved or hand-edited
/// plan can't disagree with itself.
/// </summary>
internal sealed record UpgradePlan(DateTimeOffset CreatedUtc, string SolutionPath, IReadOnlyList<PlannedUpdate> Updates)
{
    public const string PatchMinorGroupName = "patch-minor";

    public const string NotSelectedReason = "not selected by --only";

    /// <summary>In run order: the patch/minor group first, then majors by name.</summary>
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

    /// <summary>
    /// Keeps the planned updates whose ID matches one of <paramref name="only"/> (globs) or whose family is named
    /// there; the rest become Skipped. Applied to every plan, detected or loaded, so --only always means the same.
    /// </summary>
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
                .Select(u => u.Decision == UpdateDecision.Planned && !Selected(u) ? u with { Decision = UpdateDecision.Skipped, Reason = NotSelectedReason, Group = null } : u)
                .ToList(),
        };
    }
}
