using System.Text.RegularExpressions;
using UpgradeAgent.Bumping;
using UpgradeAgent.Infrastructure;

namespace UpgradeAgent.Guardrails;

/// <summary>
/// Everything the agent must not change: package version entries, TargetFramework(s), LangVersion and
/// global.json. Taken after the app's bump and compared after the agent finishes.
/// </summary>
internal static partial class BuildSettingsSnapshot
{
    public static IReadOnlySet<string> Take(string repoRoot, IEnumerable<string> relativeFiles)
    {
        var entries = new HashSet<string>(StringComparer.Ordinal);
        foreach (var relative in relativeFiles)
        {
            var path = Path.Combine(repoRoot, relative);
            if (!File.Exists(path))
            {
                continue;
            }

            var name = Path.GetFileName(relative);
            if (name.Equals("global.json", StringComparison.OrdinalIgnoreCase))
            {
                entries.Add($"{relative}: {File.ReadAllText(path).Trim()}");
                continue;
            }

            if (!MsBuildFiles.IsMsBuildFile(relative))
            {
                continue;
            }

            var text = File.ReadAllText(path);
            foreach (var entry in VersionEntryScanner.Scan(text))
            {
                entries.Add($"{relative}: {entry.Element} {entry.Id} {entry.Version ?? "(none)"}{(entry.HasVersionOverride ? " +VersionOverride" : "")}");
            }

            foreach (Match property in GuardedProperty().Matches(text))
            {
                entries.Add($"{relative}: <{property.Groups["name"].Value}> {property.Groups["value"].Value.Trim()}");
            }
        }

        return entries;
    }

    [GeneratedRegex(@"<(?<name>TargetFrameworks?|LangVersion|ManagePackageVersionsCentrally|CentralPackageTransitivePinningEnabled)\b[^>]*>(?<value>[^<]*)</\k<name>>")]
    private static partial Regex GuardedProperty();
}
