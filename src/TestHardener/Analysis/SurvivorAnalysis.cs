using System.Globalization;
using TestHardener.Config;
using TestHardener.Stryker;

namespace TestHardener.Analysis;

internal enum SkipReason
{
    StaticData,
    Logging,
    IgnoredString,
    OutsideMember,
    SourceMissing,
}

internal sealed record Survivor(
    string Id,
    string Mutator,
    Location Location,
    string Original,
    string? Replacement,
    bool IsString,
    IReadOnlyList<string> CoveringTests);

internal sealed record SurvivorGroup
{
    public int Rank { get; init; }

    public required string File { get; init; }

    public required MemberInfo Member { get; init; }

    public required IReadOnlyList<Survivor> Survivors { get; init; }

    public int FixCommits { get; init; }

    public IReadOnlyList<string> CoveringTests { get; init; } = [];

    public IReadOnlyList<string> CoveringTestFiles { get; init; } = [];

    public IReadOnlyList<string> Reasons { get; init; } = [];

    public int LogicSurvivors => Survivors.Count(s => !s.IsString);
}

internal sealed record NotMutatedMember(string File, string Member, int CompileErrors);

internal sealed record UntestedFile(string File, int Mutants);

internal sealed record TargetAnalysis(
    IReadOnlyList<SurvivorGroup> Groups,
    IReadOnlyDictionary<SkipReason, int> Skipped,
    IReadOnlyList<NotMutatedMember> NotMutated,
    IReadOnlyList<UntestedFile> Untested);

internal static class SurvivorAnalysis
{
    public static TargetAnalysis Analyze(
        MutationReport report, TargetConfig target, Func<string, string?> readSource, IReadOnlyDictionary<string, int> fixCommits)
    {
        var locators = new Dictionary<string, MemberLocator?>(StringComparer.Ordinal);
        var skipped = new Dictionary<SkipReason, int>();
        var candidates = new List<(string File, MemberInfo Member, Survivor Survivor)>();

        foreach (var mutant in report.Mutants.Where(m => m.Status == MutantStatus.Survived))
        {
            var (reason, member, survivor) = Classify(mutant, target, report, Locator(mutant.File));
            if (reason is { } skip)
            {
                skipped[skip] = skipped.GetValueOrDefault(skip) + 1;
            }
            else
            {
                candidates.Add((mutant.File, member!, survivor!));
            }
        }

        var groups = candidates
            .GroupBy(c => (c.File, c.Member.Name))
            .Select(g => Group(g.Key.File, g.First().Member, g.Select(c => c.Survivor), fixCommits.GetValueOrDefault(g.Key.File), report.TestFiles))
            .OrderByDescending(g => g.Survivors.Count(s => !s.IsString))
            .ThenByDescending(g => g.Survivors.Count)
            .ThenByDescending(g => g.FixCommits)
            .ThenBy(g => g.File, StringComparer.Ordinal)
            .ThenBy(g => g.Member.StartLine)
            .Select((g, i) => g with { Rank = i + 1 })
            .ToList();

        return new TargetAnalysis(groups, skipped, NotMutated(report, Locator), Untested(report));

        MemberLocator? Locator(string file)
        {
            if (!locators.TryGetValue(file, out var locator))
            {
                locator = readSource(file) is { } source ? MemberLocator.Parse(source) : null;
                locators[file] = locator;
            }

            return locator;
        }
    }

    private static (SkipReason? Reason, MemberInfo? Member, Survivor? Survivor) Classify(
        Mutant mutant, TargetConfig target, MutationReport report, MemberLocator? locator)
    {
        if (mutant.Static || mutant.CoveredBy.Count == 0)
        {
            return (SkipReason.StaticData, null, null);
        }

        if (locator is null)
        {
            return (SkipReason.SourceMissing, null, null);
        }

        var place = locator.Locate(mutant.Location);
        if (place.Member is not { } member)
        {
            return (SkipReason.OutsideMember, null, null);
        }

        if (member.IsLogging || place.InLoggingCall)
        {
            return (SkipReason.Logging, null, null);
        }

        if (mutant.IsStringMutation && Glob.AnyMatch(target.IgnoreStringMutationsIn, mutant.File))
        {
            return (SkipReason.IgnoredString, null, null);
        }

        var tests = mutant.CoveredBy.Select(id => report.TestNames.GetValueOrDefault(id)).OfType<string>().ToList();
        return (null, member, new Survivor(mutant.Id, mutant.Mutator, mutant.Location, place.OriginalText, mutant.Replacement, mutant.IsStringMutation, tests));
    }

    private static SurvivorGroup Group(
        string file, MemberInfo member, IEnumerable<Survivor> survivors, int fixCommits, IReadOnlyDictionary<string, string> testFiles)
    {
        var ordered = survivors
            .OrderBy(s => s.IsString)
            .ThenBy(s => s.Location.Start.Line)
            .ThenBy(s => s.Location.Start.Column)
            .ToList();
        var tests = ordered.SelectMany(s => s.CoveringTests).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        List<string> reasons = [$"{ordered.Count} surviving mutant{(ordered.Count == 1 ? "" : "s")}"];
        if (ordered.Count(s => !s.IsString) is var logic and > 0)
        {
            reasons.Add($"{logic} not string mutations");
        }

        if (fixCommits > 0)
        {
            reasons.Add(string.Create(CultureInfo.InvariantCulture, $"{fixCommits} fix commit{(fixCommits == 1 ? "" : "s")} touched the file recently"));
        }

        var files = tests
            .Select(t => testFiles.GetValueOrDefault(t))
            .OfType<string>()
            .GroupBy(f => f, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Key)
            .ToList();
        return new SurvivorGroup
        {
            File = file, Member = member, Survivors = ordered, FixCommits = fixCommits, CoveringTests = tests, CoveringTestFiles = files, Reasons = reasons,
        };
    }

    private static List<NotMutatedMember> NotMutated(MutationReport report, Func<string, MemberLocator?> locator) =>
        report.Mutants
            .Where(m => m.Status != MutantStatus.Ignored)
            .Select(m => (Mutant: m, Member: locator(m.File)?.Locate(m.Location).Member?.Name))
            .Where(x => x.Member is not null)
            .GroupBy(x => (x.Mutant.File, Member: x.Member!))
            .Where(g => g.All(x => x.Mutant.Status == MutantStatus.CompileError))
            .Select(g => new NotMutatedMember(g.Key.File, g.Key.Member, g.Count()))
            .OrderBy(n => n.File, StringComparer.Ordinal)
            .ThenBy(n => n.Member, StringComparer.Ordinal)
            .ToList();

    private static List<UntestedFile> Untested(MutationReport report) =>
        report.Mutants
            .Where(m => m.Status == MutantStatus.NoCoverage)
            .GroupBy(m => m.File, StringComparer.Ordinal)
            .Select(g => new UntestedFile(g.Key, g.Count()))
            .OrderByDescending(u => u.Mutants)
            .ThenBy(u => u.File, StringComparer.Ordinal)
            .ToList();
}
