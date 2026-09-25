using NuGet.Versioning;
using UpgradeAgent.Config;
using UpgradeAgent.Infrastructure;

namespace UpgradeAgent.Detection;

internal sealed class Planner(PolicyOptions policy, IPackageCompatibilityChecker compatibility, TimeProvider time)
{
    private readonly UpdatePolicy _policy = new(policy);
    private readonly PackageFamilies _families = new(policy.EffectiveGroups);

    public async Task<UpgradePlan> CreateAsync(OutdatedReports reports, string repoRoot, string solutionPath, CancellationToken cancellationToken)
    {
        var updates = VersionSteps.Build(reports, policy.TargetOverrides, repoRoot)
            .GroupBy(s => (Id: s.Id.ToLowerInvariant(), s.From, s.To))
            .Select(g => Decide(g.First(), g.Select(s => s.Project).Distinct().ToList()))
            .ToList();

        updates = await CheckCompatibilityAsync(updates, cancellationToken);

        var ordered = UpdateGrouper.Assign(updates, _families)
            .OrderBy(u => u.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(u => u.From)
            .ToList();
        return new UpgradePlan(time.GetUtcNow(), RepoPath.Relative(repoRoot, solutionPath), ordered);
    }

    private PlannedUpdate Decide(VersionStep step, IReadOnlyList<ProjectTarget> projects)
    {
        var verdict = _policy.Evaluate(step);
        return new PlannedUpdate(step.Id, step.From, step.To, step.Kind, projects, verdict?.Decision ?? UpdateDecision.Planned, verdict?.Reason, Group: null);
    }

    private async Task<List<PlannedUpdate>> CheckCompatibilityAsync(List<PlannedUpdate> updates, CancellationToken cancellationToken)
    {
        var results = new Dictionary<(string Id, NuGetVersion Version, string Frameworks), CompatibilityResult>();
        var checkedUpdates = new List<PlannedUpdate>(updates.Count);
        foreach (var update in updates)
        {
            if (update.Decision != UpdateDecision.Planned || update.Kind != BumpKind.Major)
            {
                checkedUpdates.Add(update);
                continue;
            }

            var frameworks = update.Projects.Select(p => p.Framework).Distinct().OrderBy(f => f.GetShortFolderName(), StringComparer.Ordinal).ToList();
            var key = (update.Id.ToLowerInvariant(), update.To, string.Join(',', frameworks.Select(f => f.GetShortFolderName())));
            if (!results.TryGetValue(key, out var result))
            {
                results[key] = result = await compatibility.CheckAsync(update.Id, update.To, frameworks, cancellationToken);
            }

            checkedUpdates.Add(result.Status switch
            {
                CompatibilityStatus.Incompatible => update with { Decision = UpdateDecision.NeedsTfmUpgrade, Reason = result.Detail },
                CompatibilityStatus.Unknown => update with { Note = $"TFM compatibility not verified ({result.Detail}); restore will check" },
                _ => update,
            });
        }

        return checkedUpdates;
    }
}
