namespace UpgradeAgent.Detection;

/// <summary>
/// Assigns planned updates to groups. Patch/minor steps share one group. Majors are grouped by family, so that
/// for example EF Core and its providers move together; if any member's major can't move (manual, skipped,
/// needs a TFM upgrade), none of them do, because a partial family move almost always breaks the build.
/// </summary>
internal static class UpdateGrouper
{
    public static IReadOnlyList<PlannedUpdate> Assign(IReadOnlyList<PlannedUpdate> updates, PackageFamilies families)
    {
        var blockers = updates
            .Where(u => u.Kind == BumpKind.Major && u.Decision != UpdateDecision.Planned && families.Find(u.Id) is not null)
            .GroupBy(u => families.Find(u.Id)!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        return updates.Select(u =>
        {
            if (u.Decision != UpdateDecision.Planned)
            {
                return u;
            }

            if (u.Kind != BumpKind.Major)
            {
                return u with { Group = UpgradePlan.PatchMinorGroupName };
            }

            var family = families.Find(u.Id);
            if (family is not null && blockers.TryGetValue(family, out var blocker))
            {
                return u with
                {
                    Decision = UpdateDecision.Skipped,
                    Reason = $"blocked: family '{family}' member {blocker.Id} can't move ({blocker.Decision.Label()}: {blocker.Reason})",
                };
            }

            return u with { Group = family ?? u.Id };
        }).ToList();
    }
}
