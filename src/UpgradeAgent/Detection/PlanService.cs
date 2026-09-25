using UpgradeAgent.Config;

namespace UpgradeAgent.Detection;

internal sealed class PlanService(PackageListRunner packageList, PolicyOptions policy, TimeProvider time)
{
    public async Task<UpgradePlan> DetectAsync(string repoRoot, string solutionPath, CancellationToken cancellationToken)
    {
        var reports = await packageList.ListAllAsync(solutionPath, policy.IncludePrerelease, cancellationToken);
        using var compatibility = NuGetPackageCompatibilityChecker.ForRepo(repoRoot);
        return await new Planner(policy, compatibility, time).CreateAsync(reports, repoRoot, solutionPath, cancellationToken);
    }
}
