using NuGet.Frameworks;
using NuGet.Versioning;
using UpgradeAgent.Build;
using UpgradeAgent.Bumping;
using UpgradeAgent.Detection;
using UpgradeAgent.Guardrails;
using UpgradeAgent.Run;

namespace UpgradeAgent.Tests.TestSupport;

internal static class TestData
{
    public const string ProjectPath = "src/App/App.csproj";

    public static readonly NuGetFramework Net10 = NuGetFramework.Parse("net10.0");

    public static NuGetVersion V(string version) => NuGetVersion.Parse(version);

    public static PlannedUpdate Update(
        string id,
        string from,
        string to,
        BumpKind kind = BumpKind.Minor,
        UpdateDecision decision = UpdateDecision.Planned,
        string? group = UpgradePlan.PatchMinorGroupName,
        string? reason = null) =>
        new(id, V(from), V(to), kind, [new ProjectTarget(ProjectPath, Net10)], decision, reason, group);

    public static UpgradePlan Plan(params PlannedUpdate[] updates) => new(DateTimeOffset.UnixEpoch, "App.slnx", updates);

    public static BuildResult Build(int errors = 0) => new(
        errors == 0,
        Enumerable.Range(0, errors).Select(i => new Diagnostic(DiagnosticSeverity.Error, "CS0117", $"error {i}", "/wt/src/A.cs", i + 1)).ToList(),
        [],
        "",
        TimeSpan.Zero);

    public static TestRunResult Tests(int passed, int failed = 0, TestInventory? inventory = null) =>
        new(failed == 0, inventory, new TestCounts(passed + failed, passed, failed, 0), "", TimeSpan.Zero);

    public static GroupResult Group(
        string name,
        GroupStatus status,
        string? commit = null,
        string? reason = null,
        GroupKind kind = GroupKind.PatchMinor,
        IReadOnlyList<VersionEdit>? edits = null,
        GuardrailReport? guardrails = null,
        FixOutcome? fix = null) =>
        new(name, kind, status, reason, commit, edits ?? [], [], guardrails, fix, TimeSpan.FromSeconds(5));

    public static GroupResult Accepted(string name, string commit, params VersionEdit[] edits) =>
        Group(name, GroupStatus.Accepted, commit, edits: edits);

    public static VersionEdit Edit(string id, string from, string to, string file = "Directory.Packages.props") => new(file, id, V(from), V(to));

    public static RunReport Report(UpgradePlan? plan = null, IReadOnlyList<GroupResult>? groups = null, string worktree = "/wt") => new(
        "20260923-120000",
        "agent/nuget-updates-20260923-1200",
        worktree,
        "/out/run-20260923-120000",
        new string('b', 40),
        "10.0.112",
        new DateTimeOffset(2026, 9, 23, 12, 0, 0, TimeSpan.Zero),
        TimeSpan.FromMinutes(4),
        plan ?? Plan(),
        groups ?? []);
}
