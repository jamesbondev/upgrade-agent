using UpgradeAgent.Config;
using UpgradeAgent.Detection;

namespace UpgradeAgent.Tests.Detection;

public class PlannerTests
{
    private const string App = "/repo/src/App/App.csproj";
    private const string AppRelative = "src/App/App.csproj";
    private const string Lib = "/repo/src/Lib/Lib.csproj";
    private const string LibRelative = "src/Lib/Lib.csproj";

    [Fact]
    public async Task PatchAndMinorUpdatesShareOneGroup()
    {
        var plan = await CreatePlanAsync(new Reports()
            .Add(App, "Newtonsoft.Json", "13.0.1", latest: "13.0.4", minor: "13.0.4", patch: "13.0.4")
            .Add(App, "Serilog", "4.1.0", latest: "4.3.0", minor: "4.3.0", patch: "4.1.1"));

        var group = Assert.Single(plan.Groups);
        Assert.Equal(UpgradePlan.PatchMinorGroupName, group.Name);
        Assert.Equal(GroupKind.PatchMinor, group.Kind);
        Assert.Equal(["Newtonsoft.Json", "Serilog"], group.Updates.Select(u => u.Id));
    }

    [Fact]
    public async Task MajorWithNewerMinorBecomesTwoSteps()
    {
        var plan = await CreatePlanAsync(new Reports()
            .Add(App, "Fixture.Lib", "1.0.0", latest: "2.0.0", minor: "1.1.0"));

        Assert.Collection(plan.Updates,
            u => Assert.Equal(("1.0.0", "1.1.0", BumpKind.Minor, "patch-minor"), (u.From, u.To, u.Kind, u.Group)),
            u => Assert.Equal(("1.1.0", "2.0.0", BumpKind.Major, "Fixture.Lib"), (u.From, u.To, u.Kind, u.Group)));
        Assert.Equal(["patch-minor", "Fixture.Lib"], plan.Groups.Select(g => g.Name));
    }

    [Fact]
    public async Task MajorWithoutNewerMinorIsOneStep()
    {
        // A package already on the newest version of its major is absent from the --highest-minor report.
        var plan = await CreatePlanAsync(new Reports()
            .Add(App, "Humanizer.Core", "2.14.1", latest: "3.0.1"));

        var update = Assert.Single(plan.Updates);
        Assert.Equal(("2.14.1", "3.0.1", BumpKind.Major), (update.From, update.To, update.Kind));
    }

    [Fact]
    public async Task ZeroMajorUsesHighestPatchForTheFirstStep()
    {
        var plan = await CreatePlanAsync(new Reports()
            .Add(App, "Early.Package", "0.3.1", latest: "0.5.0", minor: "0.4.2", patch: "0.3.4"));

        Assert.Collection(plan.Updates,
            u => Assert.Equal(("0.3.1", "0.3.4", BumpKind.Patch), (u.From, u.To, u.Kind)),
            u => Assert.Equal(("0.3.4", "0.5.0", BumpKind.Major), (u.From, u.To, u.Kind)));
    }

    [Fact]
    public async Task ProjectsOnDifferentVersionsGetSeparateSteps()
    {
        var plan = await CreatePlanAsync(new Reports()
            .Add(App, "Newtonsoft.Json", "12.0.1", latest: "13.0.4", minor: "12.0.3")
            .Add(Lib, "Newtonsoft.Json", "13.0.1", latest: "13.0.4", minor: "13.0.4"));

        Assert.Collection(plan.Updates,
            u => Assert.Equal(("12.0.1", "12.0.3", AppRelative), (u.From, u.To, Assert.Single(u.Projects).ProjectPath)),
            u => Assert.Equal(("12.0.3", "13.0.4", AppRelative), (u.From, u.To, Assert.Single(u.Projects).ProjectPath)),
            u => Assert.Equal(("13.0.1", "13.0.4", LibRelative), (u.From, u.To, Assert.Single(u.Projects).ProjectPath)));
    }

    [Fact]
    public async Task SameStepAcrossProjectsAndFrameworksIsMerged()
    {
        var plan = await CreatePlanAsync(new Reports()
            .Add(App, "Newtonsoft.Json", "13.0.1", latest: "13.0.4", framework: "net8.0")
            .Add(App, "Newtonsoft.Json", "13.0.1", latest: "13.0.4", framework: "net10.0")
            .Add(Lib, "Newtonsoft.Json", "13.0.1", latest: "13.0.4"));

        var update = Assert.Single(plan.Updates);
        Assert.Equal(3, update.Projects.Count);
    }

    [Fact]
    public async Task TargetOverridePinsTheFinalVersion()
    {
        var policy = new PolicyOptions { TargetOverrides = new(StringComparer.OrdinalIgnoreCase) { ["fixture.lib"] = "2.0.0" } };

        var plan = await CreatePlanAsync(new Reports().Add(App, "Fixture.Lib", "1.0.0", latest: "3.1.0", minor: "1.1.0"), policy);

        Assert.Equal(["1.1.0", "2.0.0"], plan.Updates.Select(u => u.To));
    }

    [Fact]
    public async Task TargetOverrideAtOrBelowCurrentProducesNothing()
    {
        var policy = new PolicyOptions { TargetOverrides = new(StringComparer.OrdinalIgnoreCase) { ["Newtonsoft.Json"] = "13.0.1" } };

        var plan = await CreatePlanAsync(new Reports().Add(App, "Newtonsoft.Json", "13.0.1", latest: "13.0.4"), policy);

        Assert.Empty(plan.Updates);
    }

    [Fact]
    public async Task InvalidTargetOverrideIsAConfigurationError()
    {
        var policy = new PolicyOptions { TargetOverrides = new(StringComparer.OrdinalIgnoreCase) { ["Newtonsoft.Json"] = "latest" } };

        await Assert.ThrowsAsync<ConfigurationException>(() =>
            CreatePlanAsync(new Reports().Add(App, "Newtonsoft.Json", "13.0.1", latest: "13.0.4"), policy));
    }

    [Fact]
    public async Task DenyRuleSkipsWithReason()
    {
        var policy = new PolicyOptions { Deny = [new DenyRule { Id = "FluentAssertions", Reason = "commercial licence from v8" }] };

        var plan = await CreatePlanAsync(new Reports().Add(App, "FluentAssertions", "6.12.0", latest: "8.0.0", minor: "6.12.2"), policy);

        Assert.All(plan.Updates, u => Assert.Equal((UpdateDecision.Skipped, "denied: commercial licence from v8"), (u.Decision, u.Reason)));
        Assert.Empty(plan.Groups);
    }

    [Fact]
    public async Task AllowListSkipsEverythingElse()
    {
        var policy = new PolicyOptions { Allow = ["Newtonsoft.*"] };

        var plan = await CreatePlanAsync(new Reports()
            .Add(App, "Newtonsoft.Json", "13.0.1", latest: "13.0.4")
            .Add(App, "Serilog", "4.1.0", latest: "4.3.0"), policy);

        Assert.Equal(UpdateDecision.Planned, plan.Updates.Single(u => u.Id == "Newtonsoft.Json").Decision);
        Assert.Equal("not in Allow list", plan.Updates.Single(u => u.Id == "Serilog").Reason);
    }

    [Fact]
    public async Task MaxAutoBumpKeepsTheMinorStepButSkipsTheMajor()
    {
        var policy = new PolicyOptions { MaxAutoBump = BumpKind.Minor };

        var plan = await CreatePlanAsync(new Reports().Add(App, "Fixture.Lib", "1.0.0", latest: "2.0.0", minor: "1.1.0"), policy);

        Assert.Collection(plan.Updates,
            u => Assert.Equal(UpdateDecision.Planned, u.Decision),
            u => Assert.Equal((UpdateDecision.Skipped, "major bump exceeds MaxAutoBump (Minor)"), (u.Decision, u.Reason)));
    }

    [Fact]
    public async Task AttemptMajorsFalseSkipsMajors()
    {
        var policy = new PolicyOptions { AttemptMajors = false };

        var plan = await CreatePlanAsync(new Reports().Add(App, "Humanizer.Core", "2.14.1", latest: "3.0.1"), policy);

        Assert.Equal(UpdateDecision.Skipped, Assert.Single(plan.Updates).Decision);
    }

    [Theory]
    [InlineData("1.*")]
    [InlineData("[1.0.0, 2.0.0)")]
    public async Task RangesAndFloatingVersionsAreManual(string requested)
    {
        var plan = await CreatePlanAsync(new Reports().Add(App, "Foo", "1.0.0", latest: "1.2.0", requested: requested));

        var update = Assert.Single(plan.Updates);
        Assert.Equal(UpdateDecision.Manual, update.Decision);
        Assert.Contains(requested, update.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FamilyMajorsShareOneGroup()
    {
        var plan = await CreatePlanAsync(new Reports()
            .Add(App, "Microsoft.EntityFrameworkCore", "8.0.10", latest: "10.0.1")
            .Add(App, "Microsoft.EntityFrameworkCore.SqlServer", "8.0.10", latest: "10.0.1")
            .Add(App, "Humanizer.Core", "2.14.1", latest: "3.0.1"));

        Assert.Equal(["efcore", "Humanizer.Core"], plan.Groups.Select(g => g.Name));
        Assert.Equal(2, plan.Groups[0].Updates.Count);
    }

    [Fact]
    public async Task IncompatibleMajorNeedsTfmUpgradeAndBlocksItsFamily()
    {
        var checker = new FakeCompatibility { ["Microsoft.EntityFrameworkCore"] = new(CompatibilityStatus.Incompatible, "supports net10.0; not net8.0") };

        var plan = await CreatePlanAsync(new Reports()
            .Add(App, "Microsoft.EntityFrameworkCore", "8.0.10", latest: "10.0.1", framework: "net8.0")
            .Add(App, "Microsoft.EntityFrameworkCore.SqlServer", "8.0.10", latest: "10.0.1", framework: "net8.0"), checker: checker);

        var core = plan.Updates.Single(u => u.Id == "Microsoft.EntityFrameworkCore");
        var provider = plan.Updates.Single(u => u.Id == "Microsoft.EntityFrameworkCore.SqlServer");
        Assert.Equal((UpdateDecision.NeedsTfmUpgrade, "supports net10.0; not net8.0"), (core.Decision, core.Reason));
        Assert.Equal(UpdateDecision.Skipped, provider.Decision);
        Assert.Contains("needs a TFM upgrade", provider.Reason, StringComparison.Ordinal);
        Assert.Empty(plan.Groups);
    }

    [Fact]
    public async Task UnknownCompatibilityStaysPlannedWithANote()
    {
        var checker = new FakeCompatibility { ["Private.Package"] = new(CompatibilityStatus.Unknown, "401 Unauthorized") };

        var plan = await CreatePlanAsync(new Reports().Add(App, "Private.Package", "1.0.0", latest: "2.0.0"), checker: checker);

        var update = Assert.Single(plan.Updates);
        Assert.Equal(UpdateDecision.Planned, update.Decision);
        Assert.Contains("401 Unauthorized", update.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompatibilityIsOnlyCheckedForPlannedMajors()
    {
        var checker = new FakeCompatibility();

        await CreatePlanAsync(new Reports()
            .Add(App, "Fixture.Lib", "1.0.0", latest: "2.0.0", minor: "1.1.0")
            .Add(App, "Newtonsoft.Json", "13.0.1", latest: "13.0.4"), checker: checker);

        Assert.Equal([("Fixture.Lib", "2.0.0")], checker.Calls);
    }

    [Fact]
    public async Task OnlySelectsByIdOrFamilyAndKeepsBothStepsOfAPackage()
    {
        var reports = new Reports()
            .Add(App, "Fixture.Lib", "1.0.0", latest: "2.0.0", minor: "1.1.0")
            .Add(App, "Microsoft.EntityFrameworkCore", "8.0.10", latest: "10.0.1")
            .Add(App, "Newtonsoft.Json", "13.0.1", latest: "13.0.4");

        var byId = await CreatePlanAsync(reports, only: ["fixture.*"]);
        var byFamily = await CreatePlanAsync(reports, only: ["efcore"]);

        Assert.Equal(["patch-minor", "Fixture.Lib"], byId.Groups.Select(g => g.Name));
        Assert.Equal(["efcore"], byFamily.Groups.Select(g => g.Name));
        Assert.Equal("not selected by --only", byFamily.Updates.Single(u => u.Id == "Newtonsoft.Json").Reason);
    }

    private static Task<UpgradePlan> CreatePlanAsync(
        Reports reports, PolicyOptions? policy = null, FakeCompatibility? checker = null, string[]? only = null) =>
        new Planner(policy ?? new PolicyOptions(), checker ?? new FakeCompatibility())
            .CreateAsync(reports.Build(), "/repo", "/repo/App.slnx", only ?? [], CancellationToken.None);

    private sealed class Reports
    {
        private readonly List<ReportedPackage> _latest = [];
        private readonly List<ReportedPackage> _minor = [];
        private readonly List<ReportedPackage> _patch = [];

        public Reports Add(
            string project, string id, string current, string latest,
            string? minor = null, string? patch = null, string framework = "net10.0", string? requested = null)
        {
            _latest.Add(new ReportedPackage(project, framework, id, requested ?? current, current, latest));
            if (minor is not null)
            {
                _minor.Add(new ReportedPackage(project, framework, id, requested ?? current, current, minor));
            }

            if (patch is not null)
            {
                _patch.Add(new ReportedPackage(project, framework, id, requested ?? current, current, patch));
            }

            return this;
        }

        public OutdatedReports Build() => new(_latest, _minor, _patch);
    }

    private sealed class FakeCompatibility : Dictionary<string, CompatibilityResult>, IPackageCompatibilityChecker
    {
        public List<(string Id, string Version)> Calls { get; } = [];

        public Task<CompatibilityResult> CheckAsync(
            string id, string version, IReadOnlyCollection<string> projectFrameworks, CancellationToken cancellationToken)
        {
            Calls.Add((id, version));
            return Task.FromResult(TryGetValue(id, out var result) ? result : new CompatibilityResult(CompatibilityStatus.Compatible));
        }
    }
}
