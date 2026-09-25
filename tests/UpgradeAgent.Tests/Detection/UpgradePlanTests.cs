using UpgradeAgent.Config;
using UpgradeAgent.Detection;
using static UpgradeAgent.Tests.Detection.PlannerTests;

namespace UpgradeAgent.Tests.Detection;

public class UpgradePlanTests
{
    private const string App = "/repo/src/App/App.csproj";

    [Fact]
    public async Task NarrowSelectsByIdOrFamilyAndKeepsBothStepsOfAPackage()
    {
        var plan = await CreatePlanAsync(new Reports()
            .Add(App, "Fixture.Lib", "1.0.0", latest: "2.0.0", minor: "1.1.0")
            .Add(App, "Microsoft.EntityFrameworkCore", "8.0.10", latest: "10.0.1")
            .Add(App, "Newtonsoft.Json", "13.0.1", latest: "13.0.4"));
        var families = new PackageFamilies(new PolicyOptions().EffectiveGroups);

        var byId = plan.Narrow(["fixture.*"], families);
        var byFamily = plan.Narrow(["efcore"], families);

        Assert.Equal(["patch-minor", "Fixture.Lib"], byId.Groups.Select(g => g.Name));
        Assert.Equal(["efcore"], byFamily.Groups.Select(g => g.Name));
        Assert.Equal(UpgradePlan.NotSelectedReason, byFamily.Updates.Single(u => u.Id == "Newtonsoft.Json").Reason);
    }

    [Fact]
    public async Task NarrowingAlsoCoversUpdatesLeftForAHuman()
    {
        var plan = await CreatePlanAsync(new Reports()
            .Add(App, "Old.Lib", "8.0.0", latest: "16.0.0")
            .Add(App, "Newtonsoft.Json", "13.0.1", latest: "13.0.4"));

        var narrowed = plan.Narrow(["Newtonsoft.Json"], new PackageFamilies(new PolicyOptions().EffectiveGroups));

        var oldLib = narrowed.Updates.Single(u => u.Id == "Old.Lib");
        Assert.Equal((UpdateDecision.Skipped, UpgradePlan.NotSelectedReason), (oldLib.Decision, oldLib.Reason));
    }

    [Fact]
    public async Task NarrowingNothingKeepsThePlan()
    {
        var plan = await CreatePlanAsync(new Reports().Add(App, "Newtonsoft.Json", "13.0.1", latest: "13.0.4"));

        Assert.Same(plan, plan.Narrow([], new PackageFamilies(new PolicyOptions().EffectiveGroups)));
    }

    [Fact]
    public async Task GroupsComeFromTheUpdatesAndPutPatchMinorFirst()
    {
        var plan = await CreatePlanAsync(new Reports()
            .Add(App, "Zeta.Lib", "1.0.0", latest: "2.0.0")
            .Add(App, "Alpha.Lib", "1.0.0", latest: "1.1.0"));

        Assert.Equal([(UpgradePlan.PatchMinorGroupName, GroupKind.PatchMinor), ("Zeta.Lib", GroupKind.Major)], plan.Groups.Select(g => (g.Name, g.Kind)));
    }
}
