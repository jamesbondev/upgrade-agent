using ReadmeChecker.Detection;
using RepoKit;

namespace ReadmeChecker.Tests.TestSupport;

internal static class FactsBuilder
{
    public static RepoFacts Readme(
        string text,
        IEnumerable<string>? files = null,
        string readmePath = "README.md",
        IEnumerable<string>? frameworks = null,
        string? sdk = null,
        IEnumerable<string>? addedProjects = null)
    {
        var fileList = (files ?? []).Concat(addedProjects ?? []).Append(readmePath).Distinct().ToList();
        var age = new ReadmeAge(new GitCommit("abc123", DateTimeOffset.UnixEpoch), 3, [.. addedProjects ?? []], []);
        return new RepoFacts(
            fileList,
            new ReadmeFile(readmePath, text, IsSymlink: false),
            fileList.Where(f => f.EndsWith("proj", StringComparison.Ordinal)).ToList(),
            new HashSet<string>(frameworks ?? [], StringComparer.OrdinalIgnoreCase),
            sdk,
            age);
    }

    public static IReadOnlyList<Signal> Signals(RepoFacts facts) => ReadmeSignals.Find(facts).Signals;
}
