using UpgradeAgent.Agent;
using UpgradeAgent.Bumping;
using UpgradeAgent.Detection;
using UpgradeAgent.Guardrails;
using UpgradeAgent.Publishing;
using UpgradeAgent.Run;

namespace UpgradeAgent.Tests.Publishing;

public class PrDescriptionTests
{
    [Fact]
    public void DescribesIncludedRejectedAndSkippedUpdates()
    {
        var markdown = PrDescription.Create(Report());

        Assert.StartsWith("# chore(deps): NuGet updates 2026-09-23 (1 group)", markdown, StringComparison.Ordinal);
        Assert.Contains("| Newtonsoft.Json | 13.0.1 | 13.0.4 | patch | ✅ included (`aaaaaaaa`) |", markdown, StringComparison.Ordinal);
        Assert.Contains("| Fixture.Lib | 1.1.0 | 2.0.0 | major | ❌ rejected |", markdown, StringComparison.Ordinal);
        Assert.Contains("| FluentAssertions | 6.12.0 | 8.0.0 | major | ⏭ skipped |", markdown, StringComparison.Ordinal);
        Assert.Contains("- ❌ **Fixture.Lib** (Fixture.Lib 1.1.0 → 2.0.0): Build: 7 error(s)", markdown, StringComparison.Ordinal);
        Assert.Contains("  - unresolved: CS1503 in StatementService.cs after 3 attempts", markdown, StringComparison.Ordinal);
        Assert.Contains("- FluentAssertions 6.12.0 → 8.0.0: skipped (denied: commercial licence from v8)", markdown, StringComparison.Ordinal);
        Assert.Contains("- test file modified: tests/A.Tests/ATests.cs", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void NeverClaimsMoreThanTheRunVerified()
    {
        var markdown = PrDescription.Create(Report());

        // The rejected group's agent claims ("fixed") must not appear as applied fixes.
        Assert.DoesNotContain("Fixes applied", markdown, StringComparison.Ordinal);
    }

    private static RunReport Report()
    {
        PlannedUpdate Update(string id, string from, string to, BumpKind kind, UpdateDecision decision, string? group, string? reason = null) =>
            new(id, from, to, kind, [new ProjectTarget("src/A/A.csproj", "net10.0")], decision, reason, group);

        var patch = Update("Newtonsoft.Json", "13.0.1", "13.0.4", BumpKind.Patch, UpdateDecision.Planned, "patch-minor");
        var major = Update("Fixture.Lib", "1.1.0", "2.0.0", BumpKind.Major, UpdateDecision.Planned, "Fixture.Lib");
        var denied = Update("FluentAssertions", "6.12.0", "8.0.0", BumpKind.Major, UpdateDecision.Skipped, null, "denied: commercial licence from v8");
        var plan = new UpgradePlan(DateTimeOffset.UtcNow, "x.slnx", [patch, major, denied],
            [new UpdateGroup("patch-minor", GroupKind.PatchMinor, [patch]), new UpdateGroup("Fixture.Lib", GroupKind.Major, [major])]);

        var accepted = new GroupResult("patch-minor", GroupKind.PatchMinor, GroupStatus.Accepted, null, new string('a', 40),
            [new VersionEdit("Directory.Packages.props", "Newtonsoft.Json", "13.0.1", "13.0.4")], [],
            new GuardrailReport([new GuardrailCheck("Build", true, "ok")], ["test file modified: tests/A.Tests/ATests.cs"]), null, TimeSpan.FromSeconds(5));
        var agentClaims = new GroupSummary([new PackageSummary("Fixture.Lib", "1.1.0", "2.0.0", "unresolved", [], [new AppliedFix("src/A.cs", "claimed")], [],
            ["CS1503 in StatementService.cs after 3 attempts"])]);
        var rejected = new GroupResult("Fixture.Lib", GroupKind.Major, GroupStatus.Rejected, "Build: 7 error(s)", null,
            [new VersionEdit("Directory.Packages.props", "Fixture.Lib", "1.1.0", "2.0.0")], [], null,
            new FixOutcome(true, "stopped", agentClaims), TimeSpan.FromMinutes(3));

        return new RunReport("20260923-120000", "agent/nuget-updates-20260923-1200", "/wt", new string('b', 40), "10.0.112",
            new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero), TimeSpan.FromMinutes(4), plan, [accepted, rejected], [new string('a', 40)]);
    }
}
