using System.Text;
using NuGet.Versioning;
using UpgradeAgent.Detection;
using UpgradeAgent.Infrastructure;
using UpgradeAgent.MsBuild;

namespace UpgradeAgent.Bumping;

internal sealed record VersionEdit(string File, string Id, NuGetVersion From, NuGetVersion To);

internal sealed record ManualUpdate(PlannedUpdate Update, string Reason);

internal sealed record BumpResult(IReadOnlyList<VersionEdit> Edits, IReadOnlyList<ManualUpdate> Manual);

internal static class VersionBumper
{
    public static BumpResult Apply(string repoRoot, IReadOnlyList<PlannedUpdate> updates)
    {
        var files = updates
            .SelectMany(u => u.Projects)
            .SelectMany(p => GoverningFiles(repoRoot, p.ProjectPath))
            .Distinct()
            .ToDictionary(f => f, ReadPreservingEncoding);

        var (edits, manual, texts) = VersionEditPlanner.Plan(
            repoRoot, files.ToDictionary(f => f.Key, f => f.Value.Text), updates, project => GoverningFiles(repoRoot, project));

        foreach (var (file, text) in texts)
        {
            File.WriteAllText(file, text, files[file].Encoding);
        }

        return new BumpResult(edits, manual);
    }

    private static (string Text, Encoding Encoding) ReadPreservingEncoding(string path)
    {
        using var reader = new StreamReader(path, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), detectEncodingFromByteOrderMarks: true);
        var text = reader.ReadToEnd();
        return (text, reader.CurrentEncoding);
    }

    private static IEnumerable<string> GoverningFiles(string repoRoot, string projectPath)
    {
        var project = Path.GetFullPath(projectPath, repoRoot);
        if (File.Exists(project))
        {
            yield return project;
        }

        foreach (var name in MsBuildFiles.VersionGoverning)
        {
            if (MsBuildFiles.FindNearest(Path.GetDirectoryName(project)!, name, repoRoot) is { } nearest)
            {
                yield return nearest;
            }
        }
    }
}

internal static class VersionEditPlanner
{
    public static (IReadOnlyList<VersionEdit> Edits, IReadOnlyList<ManualUpdate> Manual, IReadOnlyDictionary<string, string> ChangedTexts) Plan(
        string repoRoot, IReadOnlyDictionary<string, string> texts, IReadOnlyList<PlannedUpdate> updates, Func<string, IEnumerable<string>> governingFiles)
    {
        var pending = new Dictionary<string, List<(VersionValue Value, string NewText)>>(StringComparer.Ordinal);
        var edits = new List<VersionEdit>();
        var manual = new List<ManualUpdate>();

        foreach (var update in updates)
        {
            var outcomes = update.Projects
                .SelectMany(p => governingFiles(p.ProjectPath))
                .Distinct()
                .SelectMany(file => VersionEntryScanner.Scan(texts[file])
                    .Where(e => string.Equals(e.Id, update.Id, StringComparison.OrdinalIgnoreCase))
                    .Select(entry => Classify(file, RepoPath.Relative(repoRoot, file), entry, update.To)))
                .Where(o => o is not null)
                .Select(o => o!)
                .ToList();

            var reasons = outcomes.OfType<EntryOutcome.Manual>().Select(m => m.Reason).Distinct().ToList();
            if (reasons.Count > 0)
            {
                manual.Add(new ManualUpdate(update, string.Join("; ", reasons)));
                continue;
            }

            if (outcomes.Count == 0)
            {
                manual.Add(new ManualUpdate(update, "no version entry found in the project, Directory.Packages.props or Directory.Build.*"));
                continue;
            }

            foreach (var edit in outcomes.OfType<EntryOutcome.Edit>())
            {
                var fileEdits = pending.TryGetValue(edit.File, out var list) ? list : pending[edit.File] = [];
                if (!fileEdits.Any(e => e.Value.Start == edit.Value.Start))
                {
                    fileEdits.Add((edit.Value, update.To.ToNormalizedString()));
                    edits.Add(new VersionEdit(edit.RelativePath, update.Id, edit.Current, update.To));
                }
            }
        }

        var changed = pending.ToDictionary(p => p.Key, p => ApplyEdits(texts[p.Key], p.Value), StringComparer.Ordinal);
        return (edits, manual, changed);
    }

    private static EntryOutcome? Classify(string file, string relativePath, VersionEntry entry, NuGetVersion target)
    {
        if (entry.HasVersionOverride)
        {
            return new EntryOutcome.Manual($"VersionOverride in {relativePath}");
        }

        if (entry.Value is null)
        {
            return null;
        }

        if (!PlainVersion.TryParse(entry.Value.Text, out var current))
        {
            return new EntryOutcome.Manual($"version in {relativePath} is not a plain version ('{entry.Value.Text}')");
        }

        return current >= target ? new EntryOutcome.UpToDate() : new EntryOutcome.Edit(file, relativePath, entry.Value, current.Normalized());
    }

    private static string ApplyEdits(string text, List<(VersionValue Value, string NewText)> fileEdits)
    {
        var builder = new StringBuilder(text);
        foreach (var (value, newText) in fileEdits.OrderByDescending(e => e.Value.Start))
        {
            builder.Remove(value.Start, value.Length).Insert(value.Start, newText);
        }

        return builder.ToString();
    }

    private abstract record EntryOutcome
    {
        public sealed record Edit(string File, string RelativePath, VersionValue Value, NuGetVersion Current) : EntryOutcome;

        public sealed record UpToDate : EntryOutcome;

        public sealed record Manual(string Reason) : EntryOutcome;
    }
}
