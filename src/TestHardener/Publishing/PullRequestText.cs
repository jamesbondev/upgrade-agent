using System.Globalization;
using System.Text;
using RepoKit.AzureDevOps;
using TestHardener.Hardening;
using TestHardener.Stryker;

namespace TestHardener.Publishing;

internal sealed record FileScore(string File, int Detected, int Valid, int NewlyKilled)
{
    public double Before => Valid == 0 ? 0 : 100.0 * Detected / Valid;

    public double After => Valid == 0 ? 0 : 100.0 * Math.Min(Valid, Detected + NewlyKilled) / Valid;

    public static IReadOnlyList<FileScore> From(IEnumerable<MutationReport> reports, IReadOnlyList<GroupResult> kept) =>
        kept.GroupBy(g => g.Group.File, StringComparer.Ordinal)
            .Select(g =>
            {
                var mutants = reports.SelectMany(r => r.Mutants).Where(m => m.File == g.Key || m.File.EndsWith("/" + g.Key, StringComparison.Ordinal)).ToList();
                var detected = mutants.Count(m => m.Status is MutantStatus.Killed or MutantStatus.Timeout);
                var valid = detected + mutants.Count(m => m.Status is MutantStatus.Survived or MutantStatus.NoCoverage);
                return new FileScore(g.Key, detected, valid, g.Sum(r => r.Final?.Killed ?? 0));
            })
            .OrderBy(s => s.File, StringComparer.Ordinal)
            .ToList();
}

internal static class PullRequestText
{
    public static string Title(IReadOnlyList<GroupResult> kept)
    {
        var members = kept.Select(g => ShortName(g.Group.Member.Name)).Distinct(StringComparer.Ordinal).ToList();
        var tests = kept.Sum(g => g.Final?.NewTests.Count ?? 0);
        return members.Count == 1
            ? $"Add {tests} test{Plural(tests)} that catch surviving mutants in {members[0]}"
            : $"Add {tests} tests that catch surviving mutants in {members.Count} methods";
    }

    public static string CommitMessage(IReadOnlyList<GroupResult> kept, IReadOnlyList<string?> scopes)
    {
        var scope = string.Join(',', scopes.OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        var builder = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"test{(scope.Length == 0 ? "" : $"({scope})")}: cover surviving mutants")
            .AppendLine();
        foreach (var group in kept)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"- {ShortName(group.Group.Member.Name)}: {group.Final?.Killed ?? 0} mutants caught by {group.Final?.NewTests.Count ?? 0} tests");
        }

        return builder.AppendLine()
            .AppendLine("Written by TestHardener's agent, checked by build, repeated test runs and Stryker.NET; review before merging.")
            .ToString();
    }

    public static string Description(string sha, IReadOnlyList<GroupResult> kept, IReadOnlyList<FileScore> scores)
    {
        var builder = new StringBuilder()
            .AppendLine(CultureInfo.InvariantCulture, $"Stryker.NET found code where a small change (a mutant) broke no test, at commit `{sha[..Math.Min(12, sha.Length)]}`. An agent (TestHardener) wrote the tests below to catch those mutants. TestHardener then checked, in code:")
            .AppendLine()
            .AppendLine("- only these test files changed, and only by adding tests (existing tests, helpers and data rows are untouched);")
            .AppendLine("- the project builds, and the new tests pass on the current code five times in a row;")
            .AppendLine("- each new test fails when one of the mutants below is applied, as a scoped Stryker.NET run confirmed;")
            .AppendLine("- the test projects pass as a whole.")
            .AppendLine()
            .AppendLine("**The tests are written by an agent. Read each assertion: a test can lock in current behavior that is really a bug.**")
            .AppendLine()
            .AppendLine("| Method | Test | Mutants it catches |")
            .AppendLine("|---|---|---|");

        foreach (var group in kept)
        {
            foreach (var (test, kills) in group.Final?.KillsPerTest ?? new Dictionary<string, int>())
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"| {Code(ShortName(group.Group.Member.Name))} | {Code(test[(test.LastIndexOf('.') + 1)..])} | {kills} |");
            }
        }

        if (scores.Count > 0)
        {
            builder.AppendLine()
                .AppendLine("Mutation score of the files these methods are in, from the survey and the scoped runs (not a new full run):")
                .AppendLine();
            foreach (var score in scores)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"- `{Escape(score.File)}`: {score.Before:0.0}% → {score.After:0.0}%");
            }
        }

        builder.AppendLine().AppendLine("## What the agent says each test checks (unverified)").AppendLine();
        foreach (var group in kept)
        {
            foreach (var claim in group.Summary?.Tests ?? [])
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"- {Escape(claim.Name)}: {Escape(claim.Asserts)}");
            }
        }

        var remaining = kept.Sum(g => (g.Final?.Survivors.Count ?? 0) - (g.Final?.Killed ?? 0));
        if (remaining > 0)
        {
            builder.AppendLine()
                .AppendLine(CultureInfo.InvariantCulture, $"{remaining} other survivors in these methods are still not caught; the next run may take them on.");
        }

        return builder.ToString();
    }

    private static string ShortName(string member)
    {
        var parenthesis = member.IndexOf('(', StringComparison.Ordinal);
        return parenthesis > 0 ? member[..parenthesis] : member;
    }

    private static string Code(string name) => $"`{name.Replace("`", "", StringComparison.Ordinal).Replace("|", "", StringComparison.Ordinal)}`";

    private static string Plural(int count) => count == 1 ? "" : "s";

    private static string Escape(string text) => PullRequestMarkdown.Escape(text);
}
