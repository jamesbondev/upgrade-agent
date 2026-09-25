using System.Text.RegularExpressions;
using UpgradeAgent.Infrastructure;

namespace UpgradeAgent.Detection;

internal sealed class PackageFamilies
{
    private readonly IReadOnlyList<(string Name, Regex[] Patterns)> _families;

    public PackageFamilies(IReadOnlyDictionary<string, List<string>> groups) =>
        _families = groups.Select(g => (g.Key, g.Value.Select(Glob.ToRegex).ToArray())).ToList();

    public string? Find(string packageId) =>
        _families.FirstOrDefault(f => f.Patterns.Any(p => p.IsMatch(packageId))).Name;
}
