using System.Text.Json;
using UpgradeAgent.Agent.Activities;
using UpgradeAgent.Config;
using UpgradeAgent.Detection;
using UpgradeAgent.Infrastructure;
using UpgradeAgent.Run;

namespace UpgradeAgent.Replay;

internal sealed record RecordingHeader(
    string Name,
    DateTimeOffset RecordedUtc,
    string TargetCommit,
    string SdkVersion,
    string OperatingSystem,
    UpgradePlan Plan);

internal sealed record RecordedSession(IReadOnlyList<RecordedActivity> Activity, string PatchPath, FixOutcome Outcome);

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

    public (RecordingHeader Header, string? Warning) OpenForReplay(string targetCommit, string sdkVersion)
    {
        if (!Exists)
        {
            throw new ConfigurationException($"No recording at {Directory}.");
        }

        var header = LoadHeader();
        if (header.TargetCommit != targetCommit)
        {
            throw new ConfigurationException(
                $"Recording '{Name}' was made at commit {header.TargetCommit.ShortSha()}; the repo is at {targetCommit.ShortSha()}. The recorded patches only apply to the same commit. Record again.");
        }

        return (header, header.SdkVersion == sdkVersion ? null : $"recorded with SDK {header.SdkVersion}, running with {sdkVersion}. Build output may differ.");
    }

    public RecordingHeader LoadHeader() =>
        JsonSerializer.Deserialize<RecordingHeader>(File.ReadAllText(HeaderPath), JsonDefaults.Options)
            ?? throw new InvalidDataException($"Unreadable recording header: {HeaderPath}");

    public void SaveGroup(string group, IReadOnlyList<RecordedActivity> activity, string patch, FixOutcome outcome)
    {
        var folder = GroupFolder(group);
        System.IO.Directory.CreateDirectory(folder);
        File.WriteAllLines(Path.Combine(folder, "activity.jsonl"), activity.Select(a => JsonSerializer.Serialize(a, JsonDefaults.Compact)));
        File.WriteAllText(Path.Combine(folder, "agent.patch"), patch);
        File.WriteAllText(Path.Combine(folder, "outcome.json"), JsonSerializer.Serialize(outcome, JsonDefaults.Options));
    }

    public bool HasGroup(string group) => File.Exists(Path.Combine(GroupFolder(group), "outcome.json"));

    public RecordedSession LoadGroup(string group)
    {
        var folder = GroupFolder(group);
        var activity = File.ReadAllLines(Path.Combine(folder, "activity.jsonl"))
            .Where(l => l.Length > 0)
            .Select(l => JsonSerializer.Deserialize<RecordedActivity>(l, JsonDefaults.Compact)
                ?? throw new InvalidDataException($"Unreadable activity line in {folder}"))
            .ToList();
        var outcome = JsonSerializer.Deserialize<FixOutcome>(File.ReadAllText(Path.Combine(folder, "outcome.json")), JsonDefaults.Options)
            ?? throw new InvalidDataException($"Unreadable outcome in {folder}");
        return new RecordedSession(activity, Path.Combine(folder, "agent.patch"), outcome);
    }

    private string GroupFolder(string group) => Path.Combine(Directory, "groups", RepoPath.SafeFileName(group));
}
