using UpgradeAgent.Agent;
using UpgradeAgent.Detection;
using UpgradeAgent.Guardrails;
using UpgradeAgent.Publishing;
using UpgradeAgent.Run;
using UpgradeAgent.Tests.TestSupport;

namespace UpgradeAgent.Tests.Publishing;

public class PrDescriptionTests
{
    private const int Limit = 4000;

    [Fact]
    public void DescribesIncludedRejectedAndSkippedUpdates()
    {
        var markdown = PrDescription.Create(Report());

        Assert.StartsWith("# chore(deps): NuGet updates 2026-09-23 (1 group)", markdown, StringComparison.Ordinal);
        Assert.Contains("| Newtonsoft.Json | 13.0.1 | 13.0.4 | patch | ✅ included (`aaaaaaaa`) |", markdown, StringComparison.Ordinal);
        Assert.Contains("| Fixture.Lib | 1.1.0 | 2.0.0 | major | ❌ rejected |", markdown, StringComparison.Ordinal);
        Assert.Contains("| FluentAssertions | 6.12.0 | 8.0.0 | major | ⏭ skipped |", markdown, StringComparison.Ordinal);
        Assert.Contains("- ❌ **Fixture.Lib** (Fixture.Lib 1.1.0 → 2.0.0): Build: 7 error(s)", markdown, StringComparison.Ordinal);
        Assert.Contains("  - unresolved (agent's account): CS1503 in StatementService.cs after 3 attempts", markdown, StringComparison.Ordinal);
        Assert.Contains("- FluentAssertions 6.12.0 → 8.0.0: skipped (denied: commercial licence from v8)", markdown, StringComparison.Ordinal);
        Assert.Contains("**Reviewer attention: test files changed**", markdown, StringComparison.Ordinal);
        Assert.Contains("- test file modified: tests/A.Tests/ATests.cs", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void RejectedGroupsNeverShowTheAgentsClaimedFixes()
    {
        var markdown = PrDescription.Create(Report());

        Assert.DoesNotContain("Fixes applied", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAgentsAccountIsMarkedUnverifiedAndCannotReshapeThePage()
    {
        var summary = new GroupSummary
        {
            Packages =
            [
                new PackageSummary
                {
                    Id = "Fixture.Lib", From = "1.1.0", To = "2.0.0", Status = PackageStatus.Fixed,
                    BreakingChanges = ["Format renamed\n## Approved by security | <b>ship it</b>"],
                },
            ],
        };
        var accepted = TestData.Group("Fixture.Lib", GroupStatus.Accepted, new string('c', 40), kind: GroupKind.Major,
            fix: new FixOutcome(true, "finished", summary));

        var markdown = PrDescription.Create(TestData.Report(groups: [accepted]));

        Assert.Contains("_(unverified; the diff is the record)_", markdown, StringComparison.Ordinal);
        Assert.Contains(@"- Format renamed \#\# Approved by security \| \<b\>ship it\</b\>", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("\n## Approved", markdown, StringComparison.Ordinal);
    }

    [Fact]
    public void ShortDescriptionsAreUnchanged()
    {
        Assert.Equal("# Title\nbody", PrDescription.Fit("# Title\nbody", Limit));
    }

    [Fact]
    public void LongDescriptionsDropDetailsFirst()
    {
        var markdown = "# Title\n" + $"<details><summary>x</summary>\n{new string('d', 5000)}\n</details>\n" + "## Run\n- ok\n";

        Assert.Equal("# Title\n## Run\n- ok\n", PrDescription.Fit(markdown, Limit));
    }

    [Fact]
    public void VeryLongDescriptionsAreCutAtALineWithANotice()
    {
        var markdown = string.Join('\n', Enumerable.Range(0, 400).Select(i => $"- line {i} with some text"));

        var fitted = PrDescription.Fit(markdown, Limit);

        Assert.True(fitted.Length <= Limit);
        Assert.EndsWith("The full report is the first comment._", fitted, StringComparison.Ordinal);
        Assert.Contains("4,000-character limit", fitted, StringComparison.Ordinal);
        Assert.Contains("- line 1 with some text\n", fitted, StringComparison.Ordinal);
        Assert.DoesNotContain("- line 399", fitted, StringComparison.Ordinal);
    }

    private static RunReport Report()
    {
        var patch = TestData.Update("Newtonsoft.Json", "13.0.1", "13.0.4", BumpKind.Patch);
        var major = TestData.Update("Fixture.Lib", "1.1.0", "2.0.0", BumpKind.Major, group: "Fixture.Lib");
        var denied = TestData.Update("FluentAssertions", "6.12.0", "8.0.0", BumpKind.Major, UpdateDecision.Skipped, group: null, reason: "denied: commercial licence from v8");

        var guardrails = new GuardrailReport(
            [new GuardrailCheck("Build", true, "ok")],
            [new ReviewNote(ReviewNoteKind.TestFileModified, "test file modified: tests/A.Tests/ATests.cs")]);
        var accepted = TestData.Group("patch-minor", GroupStatus.Accepted, new string('a', 40),
            edits: [TestData.Edit("Newtonsoft.Json", "13.0.1", "13.0.4")], guardrails: guardrails);

        var agentClaims = new GroupSummary
        {
            Packages =
            [
                new PackageSummary
                {
                    Id = "Fixture.Lib", From = "1.1.0", To = "2.0.0", Status = PackageStatus.Unresolved,
                    Fixes = [new AppliedFix { File = "src/A.cs", Reason = "claimed" }],
                    Unresolved = ["CS1503 in StatementService.cs after 3 attempts"],
                },
            ],
        };
        var rejected = TestData.Group("Fixture.Lib", GroupStatus.Rejected, reason: "Build: 7 error(s)", kind: GroupKind.Major,
            edits: [TestData.Edit("Fixture.Lib", "1.1.0", "2.0.0")], fix: new FixOutcome(true, "stopped", agentClaims));

        return TestData.Report(TestData.Plan(patch, major, denied), [accepted, rejected]);
    }
}
