using System.Text.Json;
using UpgradeAgent.Detection;
using UpgradeAgent.Run;
using UpgradeAgent.Infrastructure;

namespace UpgradeAgent.Replay;

internal sealed record RecordingHeader(
    string Name,
    DateTimeOffset RecordedUtc,
    string TargetCommit,
    string SdkVersion,
    string OperatingSystem,
    UpgradePlan Plan);

/// <summary>One rendered line of agent activity and when it appeared, relative to the start of the group.</summary>
internal sealed record ActivityLine(double OffsetSeconds, string Markup);

/// <summary>
/// A recording lives in <c>recordings/&lt;name&gt;/</c>: header.json plus, per group the agent worked on,
/// the activity it showed, the patch of its changes, and its outcome.
/// </summary>
internal sealed class Recording(string directory)
{
    public string Directory { get; } = directory;

    public string Name => Path.GetFileName(Directory);

    public static Recording At(string recordingsRoot, string name) =>
        new(Path.Combine(Path.GetFullPath(recordingsRoot), name));

    public bool Exists => File.Exists(HeaderPath);

    private string HeaderPath => Path.Combine(Directory, "header.json");

    public void SaveHeader(RecordingHeader header)
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(HeaderPath, JsonSerializer.Serialize(header, JsonDefaults.Options));
    }

    public RecordingHeader LoadHeader() =>
        JsonSerializer.Deserialize<RecordingHeader>(File.ReadAllText(HeaderPath), JsonDefaults.Options)
            ?? throw new InvalidDataException($"Unreadable recording header: {HeaderPath}");

    public void SaveGroup(string group, IReadOnlyList<ActivityLine> activity, string patch, FixOutcome outcome)
    {
        var folder = GroupFolder(group);
        System.IO.Directory.CreateDirectory(folder);
        File.WriteAllLines(Path.Combine(folder, "activity.jsonl"), activity.Select(a => JsonSerializer.Serialize(a, JsonDefaults.Compact)));
        File.WriteAllText(Path.Combine(folder, "agent.patch"), patch);
        File.WriteAllText(Path.Combine(folder, "outcome.json"), JsonSerializer.Serialize(outcome, JsonDefaults.Options));
    }

    public bool HasGroup(string group) => File.Exists(Path.Combine(GroupFolder(group), "outcome.json"));

    public (IReadOnlyList<ActivityLine> Activity, string PatchPath, FixOutcome Outcome) LoadGroup(string group)
    {
        var folder = GroupFolder(group);
        var activity = File.ReadAllLines(Path.Combine(folder, "activity.jsonl"))
            .Where(l => l.Length > 0)
            .Select(l => JsonSerializer.Deserialize<ActivityLine>(l, JsonDefaults.Compact)!)
            .ToList();
        var outcome = JsonSerializer.Deserialize<FixOutcome>(File.ReadAllText(Path.Combine(folder, "outcome.json")), JsonDefaults.Options)!;
        return (activity, Path.Combine(folder, "agent.patch"), outcome);
    }

    private string GroupFolder(string group) =>
        Path.Combine(Directory, "groups", RepoPath.SafeFileName(group));
}
