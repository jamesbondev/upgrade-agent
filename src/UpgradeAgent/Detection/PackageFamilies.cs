using System.Text.RegularExpressions;
using UpgradeAgent.Infrastructure;

namespace UpgradeAgent.Detection;

/// <summary>Package families that must move together (Policy:Groups), with their ID globs compiled once.</summary>
internal sealed class PackageFamilies
{
    private readonly IReadOnlyList<(string Name, Regex[] Patterns)> _families;

    public PackageFamilies(IReadOnlyDictionary<string, List<string>> groups) =>
        _families = groups.Select(g => (g.Key, g.Value.Select(Glob.ToRegex).ToArray())).ToList();

    /// <summary>The family <paramref name="packageId"/> belongs to, or null for a standalone package.</summary>
    public string? Find(string packageId) =>
        _families.FirstOrDefault(f => f.Patterns.Any(p => p.IsMatch(packageId))).Name;
}
