using System.Text;
using NuGet.Versioning;
using UpgradeAgent.Detection;

namespace UpgradeAgent.Bumping;

public sealed record VersionEdit(string File, string Id, string From, string To);

public sealed record ManualUpdate(PlannedUpdate Update, string Reason);

public sealed record BumpResult(IReadOnlyList<VersionEdit> Edits, IReadOnlyList<ManualUpdate> Manual);

/// <summary>
/// Applies a group's version steps to the files that govern each project: the project file, the nearest
/// Directory.Packages.props, then Directory.Build.props/targets. An entry is edited when it holds the step's
/// "from" version, or an older one (an earlier step for that package was rejected). Anything else is left
/// for a human.
/// </summary>
public static class VersionBumper
{
    private static readonly string[] InheritedFiles = ["Directory.Packages.props", "Directory.Build.props", "Directory.Build.targets"];

    public static BumpResult Apply(string repoRoot, IReadOnlyList<PlannedUpdate> updates)
    {
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        var pending = new Dictionary<string, List<(int Start, int Length, string Value)>>(StringComparer.Ordinal);
        var edits = new List<VersionEdit>();
        var manual = new List<ManualUpdate>();

        foreach (var update in updates)
        {
            var target = NuGetVersion.Parse(update.To);
            string? manualReason = null;
            var found = false;

            foreach (var file in update.Projects.SelectMany(p => GoverningFiles(repoRoot, p.ProjectPath)).Distinct())
            {
                if (!texts.TryGetValue(file, out var text))
                {
                    text = texts[file] = File.ReadAllText(file);
                }

                var relative = Path.GetRelativePath(repoRoot, file).Replace('\\', '/');
                foreach (var entry in VersionEntryScanner.Scan(text).Where(e => string.Equals(e.Id, update.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    if (entry.HasVersionOverride)
                    {
                        manualReason = $"VersionOverride in {relative}";
                        continue;
                    }

                    if (entry.Version is null)
                    {
                        continue;
                    }

                    if (!NuGetVersion.TryParse(entry.Version, out var current))
                    {
                        manualReason = $"version in {relative} is not a plain version ('{entry.Version}')";
                        continue;
                    }

                    found = true;
                    if (current >= target)
                    {
                        continue;
                    }

                    var edit = (entry.ValueStart!.Value, entry.ValueLength!.Value, target.ToNormalizedString());
                    var fileEdits = pending.TryGetValue(file, out var list) ? list : pending[file] = [];
                    if (!fileEdits.Contains(edit))
                    {
                        fileEdits.Add(edit);
                        edits.Add(new VersionEdit(relative, update.Id, current.ToNormalizedString(), target.ToNormalizedString()));
                    }
                }
            }

            if (manualReason is not null)
            {
                manual.Add(new ManualUpdate(update, manualReason));
            }
            else if (!found)
            {
                manual.Add(new ManualUpdate(update, "no version entry found in the project, Directory.Packages.props or Directory.Build.*"));
            }
        }

        foreach (var (file, fileEdits) in pending)
        {
            WritePreservingEncoding(file, texts[file], fileEdits);
        }

        return new BumpResult(edits, manual);
    }

    private static IEnumerable<string> GoverningFiles(string repoRoot, string projectPath)
    {
        var project = Path.GetFullPath(projectPath, repoRoot);
        if (File.Exists(project))
        {
            yield return project;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repoRoot));
        foreach (var name in InheritedFiles)
        {
            // MSBuild imports only the nearest one of each (unless it imports its parent explicitly).
            for (var directory = Path.GetDirectoryName(project); directory is not null && directory.Length >= root.Length; directory = Path.GetDirectoryName(directory))
            {
                var candidate = Path.Combine(directory, name);
                if (File.Exists(candidate))
                {
                    yield return candidate;
                    break;
                }
            }
        }
    }

    private static void WritePreservingEncoding(string file, string text, List<(int Start, int Length, string Value)> fileEdits)
    {
        var builder = new StringBuilder(text);
        foreach (var (start, length, value) in fileEdits.OrderByDescending(e => e.Start))
        {
            builder.Remove(start, length).Insert(start, value);
        }

        var bytes = File.ReadAllBytes(file);
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        File.WriteAllText(file, builder.ToString(), new UTF8Encoding(hasBom));
    }
}
