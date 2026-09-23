namespace UpgradeAgent.Detection;

public enum UpdateDecision
{
    Planned,
    Skipped,
    Manual,
    NeedsTfmUpgrade,
}

public enum GroupKind
{
    PatchMinor,
    Major,
}

/// <param name="ProjectPath">Relative to the repo root, with forward slashes.</param>
public sealed record ProjectTarget(string ProjectPath, string Framework);

/// <summary>
/// One version step for one package. A package with a newer minor and a newer major produces two
/// steps: current → latest minor (in the patch/minor group), then latest minor → major.
/// </summary>
public sealed record PlannedUpdate(
    string Id,
    string From,
    string To,
    BumpKind Kind,
    IReadOnlyList<ProjectTarget> Projects,
    UpdateDecision Decision,
    string? Reason,
    string? Group);

public sealed record UpdateGroup(string Name, GroupKind Kind, IReadOnlyList<PlannedUpdate> Updates);

public sealed record UpgradePlan(
    DateTimeOffset CreatedUtc,
    string SolutionPath,
    IReadOnlyList<PlannedUpdate> Updates,
    IReadOnlyList<UpdateGroup> Groups)
{
    public const string PatchMinorGroupName = "patch-minor";
}
