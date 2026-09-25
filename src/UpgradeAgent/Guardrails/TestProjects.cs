using UpgradeAgent.Infrastructure;

namespace UpgradeAgent.Guardrails;

internal static class TestProjects
{
    private static readonly string[] Markers =
    [
        "Microsoft.NET.Test.Sdk", "<IsTestProject>true", "\"MSTest.Sdk", "Include=\"xunit", "Include=\"NUnit", "Include=\"MSTest", "Include=\"TUnit",
    ];

    /// <summary>Tracked files that live under a test project's folder.</summary>
    public static IReadOnlySet<string> FindTestFiles(string repoRoot, IReadOnlyList<string> trackedFiles)
    {
        var testDirectories = trackedFiles
            .Where(MsBuildFiles.IsProjectFile)
            .Where(f => IsTestProject(Path.Combine(repoRoot, f)))
            .Select(f => RepoPath.Normalize(Path.GetDirectoryName(f) ?? ""))
            .ToList();

        return trackedFiles
            .Where(MsBuildFiles.IsSourceFile)
            .Where(f => testDirectories.Any(d => d.Length == 0 || f.StartsWith(d + "/", StringComparison.Ordinal)))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static bool IsTestProject(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var text = File.ReadAllText(path);
        return Markers.Any(m => text.Contains(m, StringComparison.OrdinalIgnoreCase));
    }
}
