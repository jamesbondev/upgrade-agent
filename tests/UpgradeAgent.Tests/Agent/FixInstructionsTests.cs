using UpgradeAgent.Agent;
using UpgradeAgent.Build;
using UpgradeAgent.Detection;

namespace UpgradeAgent.Tests.Agent;

public class FixInstructionsTests
{
    private static readonly UpdateGroup Group = new("Fixture.Lib", GroupKind.Major,
        [new PlannedUpdate("Fixture.Lib", "1.1.0", "2.0.0", BumpKind.Major, [], UpdateDecision.Planned, null, "Fixture.Lib")]);

    private static readonly BuildResult FailedBuild = new(false,
        [new Diagnostic("error", "CS0117", "'ValueFormatter' does not contain a definition for 'Format'", "/wt/src/App/A.cs", 19)], [], "", TimeSpan.Zero);

    [Fact]
    public void InlinesSmallMigrationNotesAndListsErrorsRelativeToTheWorktree()
    {
        var docs = new PackageDocs("Fixture.Lib", "2.0.0", "/pkg", ["/pkg/MIGRATION.md", "/pkg/README.md"], null, null, null);

        var (prompt, requiredReads) = FixInstructions.Task(Group, docs: [docs], FailedBuild, null, "/wt", _ => "Use FormatValue.");

        Assert.Contains("- Fixture.Lib 1.1.0 -> 2.0.0 (major)", prompt, StringComparison.Ordinal);
        Assert.Contains("=== MIGRATION.md from Fixture.Lib 2.0.0 ===\nUse FormatValue.", prompt.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("also available: /pkg/README.md", prompt, StringComparison.Ordinal);
        Assert.Contains("- src/App/A.cs:19 CS0117:", prompt, StringComparison.Ordinal);
        Assert.Empty(requiredReads);
    }

    [Fact]
    public void LargeMigrationNotesMustBeReadFirst()
    {
        var docs = new PackageDocs("Big.Lib", "3.0.0", "/pkg", ["/pkg/MIGRATION.md"], null, null, null);

        var (prompt, requiredReads) = FixInstructions.Task(Group, [docs], FailedBuild, null, "/wt", _ => new string('x', FixInstructions.InlineDocBudget + 1));

        Assert.Equal(["/pkg/MIGRATION.md"], requiredReads);
        Assert.Contains("READ FIRST /pkg/MIGRATION.md", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void PatchMinorGroupsLeaveDeprecationWarnings()
    {
        Assert.Contains("Leave deprecation", FixInstructions.System("x.slnx", TestRunnerMode.VSTest, GroupKind.PatchMinor), StringComparison.Ordinal);
        Assert.DoesNotContain("Leave deprecation", FixInstructions.System("x.slnx", TestRunnerMode.VSTest, GroupKind.Major), StringComparison.Ordinal);
    }

    [Fact]
    public void UsesTheTestingPlatformCommandWhenConfigured()
    {
        Assert.Contains("dotnet test --solution x.slnx --no-build", FixInstructions.System("x.slnx", TestRunnerMode.TestingPlatform, GroupKind.Major), StringComparison.Ordinal);
    }
}
