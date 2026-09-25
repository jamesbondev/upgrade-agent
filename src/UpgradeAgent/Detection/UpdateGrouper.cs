namespace UpgradeAgent.Detection;

internal static class UpdateGrouper
{
    public static IReadOnlyList<PlannedUpdate> Assign(IReadOnlyList<PlannedUpdate> updates, PackageFamilies families)
    {
        var blockers = updates
            .Where(u => u.Kind == BumpKind.Major && u.Decision is UpdateDecision.Manual or UpdateDecision.NeedsTfmUpgrade && families.Find(u.Id) is not null)
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
