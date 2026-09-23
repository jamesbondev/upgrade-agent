using NuGet.Versioning;
using UpgradeAgent.Config;
using UpgradeAgent.Infrastructure;

namespace UpgradeAgent.Detection;

/// <summary>Turns <c>dotnet package list</c> reports into an ordered, policy-checked plan. Deterministic; no model involved.</summary>
public sealed class Planner(PolicyOptions policy, IPackageCompatibilityChecker compatibility, TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <param name="repoRoot">Project and solution paths are stored relative to this, so a saved plan can be replayed in another worktree.</param>
    public async Task<UpgradePlan> CreateAsync(
        OutdatedReports reports,
        string repoRoot,
        string solutionPath,
        IReadOnlyCollection<string> only,
        CancellationToken cancellationToken)
    {
        var steps = reports.Latest.SelectMany(p => CreateSteps(p, reports, repoRoot)).ToList();

        var updates = steps
            .GroupBy(s => (Id: s.Id.ToLowerInvariant(), From: s.From.ToNormalizedString(), To: s.To.ToNormalizedString()))
            .Select(g => Decide(g.First(), g.Select(s => s.Project).Distinct().ToList(), only))
            .ToList();

        updates = await CheckCompatibilityAsync(updates, cancellationToken);
        updates = AssignGroups(updates);

        var groups = updates
            .Where(u => u.Decision == UpdateDecision.Planned)
            .GroupBy(u => u.Group!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new UpdateGroup(
                g.Key,
                g.Key == UpgradePlan.PatchMinorGroupName ? GroupKind.PatchMinor : GroupKind.Major,
                g.OrderBy(u => u.Id, StringComparer.OrdinalIgnoreCase).ToList()))
            .OrderBy(g => g.Kind)
            .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var ordered = updates
            .OrderBy(u => u.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(u => NuGetVersion.Parse(u.From))
            .ToList();

        return new UpgradePlan(_time.GetUtcNow(), Relative(repoRoot, solutionPath), ordered, groups);
    }

    private sealed record Step(string Id, NuGetVersion From, NuGetVersion To, BumpKind Kind, ProjectTarget Project, string? ManualReason);

    private IEnumerable<Step> CreateSteps(ReportedPackage package, OutdatedReports reports, string repoRoot)
    {
        if (!NuGetVersion.TryParse(package.ResolvedVersion, out var current)
            || !NuGetVersion.TryParse(package.LatestVersion, out var latest))
        {
            yield break;
        }

        var project = new ProjectTarget(Relative(repoRoot, package.ProjectPath), package.Framework);
        var final = latest;
        if (policy.TargetOverrides.TryGetValue(package.Id, out var overrideVersion))
        {
            final = NuGetVersion.TryParse(overrideVersion, out var parsed)
                ? parsed
                : throw new ConfigurationException($"Policy:TargetOverrides:{package.Id} is not a valid version: '{overrideVersion}'.");
        }

        if (final <= current)
        {
            yield break;
        }

        var kind = BumpClassifier.Classify(current, final);

        if (!BumpClassifier.IsPlainVersion(package.RequestedVersion))
        {
            yield return new Step(package.Id, current, final, kind, project, $"version is a range or floating ('{package.RequestedVersion}')");
            yield break;
        }

        if (kind == BumpKind.Major && FindIntermediate(package, current, final, reports) is { } intermediate)
        {
            yield return new Step(package.Id, current, intermediate, BumpClassifier.Classify(current, intermediate), project, null);
            yield return new Step(package.Id, intermediate, final, BumpKind.Major, project, null);
            yield break;
        }

        yield return new Step(package.Id, current, final, kind, project, null);
    }

    /// <summary>
    /// The newest non-breaking version before the major: latest minor for 1.0+, latest patch for 0.x.
    /// A package with nothing newer in its current line is absent from that report.
    /// </summary>
    private static NuGetVersion? FindIntermediate(ReportedPackage package, NuGetVersion current, NuGetVersion final, OutdatedReports reports)
    {
        var source = current.Major == 0 ? reports.HighestPatch : reports.HighestMinor;
        var match = source.FirstOrDefault(p =>
            string.Equals(p.Id, package.Id, StringComparison.OrdinalIgnoreCase)
            && string.Equals(p.ProjectPath, package.ProjectPath, StringComparison.Ordinal)
            && string.Equals(p.Framework, package.Framework, StringComparison.OrdinalIgnoreCase));

        return match is not null
            && NuGetVersion.TryParse(match.LatestVersion, out var candidate)
            && candidate > current
            && candidate < final
            && BumpClassifier.Classify(current, candidate) != BumpKind.Major
                ? candidate
                : null;
    }

    private PlannedUpdate Decide(Step step, IReadOnlyList<ProjectTarget> projects, IReadOnlyCollection<string> only)
    {
        var (decision, reason) = Evaluate(step, only);
        return new PlannedUpdate(
            step.Id, step.From.ToNormalizedString(), step.To.ToNormalizedString(), step.Kind, projects, decision, reason, Group: null);
    }

    private (UpdateDecision, string?) Evaluate(Step step, IReadOnlyCollection<string> only)
    {
        var deny = policy.Deny.FirstOrDefault(d => Glob.IsMatch(d.Id, step.Id));
        if (deny is not null)
        {
            return (UpdateDecision.Skipped, deny.Reason is null ? "denied by policy" : $"denied: {deny.Reason}");
        }

        if (policy.Allow.Count > 0 && !Glob.IsMatchAny(policy.Allow, step.Id))
        {
            return (UpdateDecision.Skipped, "not in Allow list");
        }

        if (only.Count > 0 && !Glob.IsMatchAny(only, step.Id) && !only.Any(o => string.Equals(o, FamilyOf(step.Id), StringComparison.OrdinalIgnoreCase)))
        {
            return (UpdateDecision.Skipped, "not selected by --only");
        }

        if (step.ManualReason is not null)
        {
            return (UpdateDecision.Manual, step.ManualReason);
        }

        if (step.Kind > policy.MaxAutoBump)
        {
            return (UpdateDecision.Skipped, $"{step.Kind.ToString().ToLowerInvariant()} bump exceeds MaxAutoBump ({policy.MaxAutoBump})");
        }

        if (step.Kind == BumpKind.Major && !policy.AttemptMajors)
        {
            return (UpdateDecision.Skipped, "major bumps disabled (AttemptMajors=false)");
        }

        return (UpdateDecision.Planned, null);
    }

    private async Task<List<PlannedUpdate>> CheckCompatibilityAsync(List<PlannedUpdate> updates, CancellationToken cancellationToken)
    {
        var checkedUpdates = new List<PlannedUpdate>(updates.Count);
        foreach (var update in updates)
        {
            if (update.Decision != UpdateDecision.Planned || update.Kind != BumpKind.Major)
            {
                checkedUpdates.Add(update);
                continue;
            }

            var frameworks = update.Projects.Select(p => p.Framework).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var result = await compatibility.CheckAsync(update.Id, update.To, frameworks, cancellationToken);
            checkedUpdates.Add(result.Status switch
            {
                CompatibilityStatus.Incompatible => update with { Decision = UpdateDecision.NeedsTfmUpgrade, Reason = result.Detail },
                CompatibilityStatus.Unknown => update with { Reason = $"TFM compatibility not verified ({result.Detail}); restore will check" },
                _ => update,
            });
        }

        return checkedUpdates;
    }

    /// <summary>
    /// Patch/minor steps share one group. Majors are grouped by family so that, for example, EF Core and
    /// its providers move together; if one family member can't move, none of them do.
    /// </summary>
    private List<PlannedUpdate> AssignGroups(List<PlannedUpdate> updates)
    {
        var blockedFamilies = updates
            .Where(u => u.Kind == BumpKind.Major && u.Decision == UpdateDecision.NeedsTfmUpgrade && FamilyOf(u.Id) is not null)
            .GroupBy(u => FamilyOf(u.Id)!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.OrdinalIgnoreCase);

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

            var family = FamilyOf(u.Id);
            if (family is not null && blockedFamilies.TryGetValue(family, out var blocker))
            {
                return u with { Decision = UpdateDecision.Skipped, Reason = $"blocked: family '{family}' member {blocker} needs a TFM upgrade" };
            }

            return u with { Group = family ?? u.Id };
        }).ToList();
    }

    private static string Relative(string repoRoot, string path) =>
        Path.GetRelativePath(repoRoot, path).Replace('\\', '/');

    private string? FamilyOf(string id) =>
        policy.EffectiveGroups.FirstOrDefault(g => Glob.IsMatchAny(g.Value, id)).Key;
}
