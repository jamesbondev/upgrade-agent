using System.Globalization;
using System.Text.RegularExpressions;
using ReadmeChecker.Detection;
using RepoKit;

namespace ReadmeChecker.Fixing;

internal sealed record Verification(IReadOnlyList<string> Problems, string NewText, string Diff)
{
    public bool Passed => Problems.Count == 0;
}

internal static partial class ReadmeVerifier
{
    private static readonly SignalKind[] ReferenceKinds = [SignalKind.BrokenLink, SignalKind.MissingPath, SignalKind.MissingCommandTarget];

    public static async Task<Verification> VerifyAsync(RepoWorkspace workspace, RepoFacts facts, double minKeptRatio, CancellationToken cancellationToken)
    {
        var readme = facts.Readme!;
        var problems = new List<string>();

        var changed = (await workspace.Git.StatusAsync(workspace.Path, includeIgnored: false, cancellationToken))
            .Select(line => line.Length > 3 ? line[3..].Trim('"') : line)
            .ToList();
        if (changed.Count == 0)
        {
            return new Verification(["the agent didn't change the README"], readme.Text, "");
        }

        var others = changed.Where(p => p != readme.Path).ToList();
        if (others.Count > 0)
        {
            problems.Add($"other files changed: {string.Join(", ", others)}");
        }

        var newText = await File.ReadAllTextAsync(Path.Combine(workspace.Path, readme.Path), cancellationToken);
        var kept = KeptRatio(readme.Text, newText);
        if (kept < minKeptRatio)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"the README kept only {kept:P0} of its lines (at least {minKeptRatio:P0} must stay)"));
        }

        var before = await SignalsAsync(facts, workspace, cancellationToken);
        var after = await SignalsAsync(facts with { Readme = readme with { Text = newText } }, workspace, cancellationToken);
        var beforeKeys = before.Select(Key).ToHashSet();
        var added = after.Where(s => ReferenceKinds.Contains(s.Kind) && !beforeKeys.Contains(Key(s))).Select(s => s.Text).Distinct().ToList();
        if (added.Count > 0)
        {
            problems.Add($"it refers to things that aren't in the repository: {string.Join(", ", added)}");
        }

        var stillBroken = after.Where(s => s.Definitive && beforeKeys.Contains(Key(s))).Select(s => s.Text).Distinct().ToList();
        if (stillBroken.Count > 0)
        {
            problems.Add($"broken links are still there: {string.Join(", ", stillBroken)}");
        }

        var knownHosts = Hosts(readme.Text).Concat(await RepoHostsAsync(workspace, cancellationToken)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newHosts = Hosts(newText).Where(h => !knownHosts.Contains(h)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (newHosts.Count > 0)
        {
            problems.Add($"it links to sites that aren't mentioned anywhere in the repository: {string.Join(", ", newHosts)}");
        }

        var diff = await workspace.Git.RunAsync(workspace.Path, ["diff", "--", readme.Path], cancellationToken);
        return new Verification(problems, newText, diff);
    }

    internal static double KeptRatio(string before, string after)
    {
        var original = Lines(before);
        if (original.Count == 0)
        {
            return 1;
        }

        var remaining = Lines(after).GroupBy(l => l).ToDictionary(g => g.Key, g => g.Count());
        var kept = 0;
        foreach (var line in original)
        {
            if (remaining.TryGetValue(line, out var count) && count > 0)
            {
                remaining[line] = count - 1;
                kept++;
            }
        }

        return (double)kept / original.Count;
    }

    internal static IEnumerable<string> Hosts(string text) =>
        Url().Matches(text).Select(m => m.Groups["host"].Value.ToLowerInvariant());

    private static List<string> Lines(string text) =>
        text.ReplaceLineEndings("\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    private static (SignalKind Kind, string Text) Key(Signal signal) => (signal.Kind, signal.Text);

    private static async Task<IReadOnlyList<Signal>> SignalsAsync(RepoFacts facts, RepoWorkspace workspace, CancellationToken cancellationToken) =>
        (await IgnoredPaths.DropIgnoredAsync(ReadmeSignals.Find(facts), workspace.Path, workspace.Git, cancellationToken)).Signals;

    private static async Task<IEnumerable<string>> RepoHostsAsync(RepoWorkspace workspace, CancellationToken cancellationToken)
    {
        var result = await workspace.Git.TryRunAsync(
            workspace.Path, ["grep", "-I", "-h", "-o", "-i", "-E", "https?://[a-z0-9.-]+", "HEAD", "--", "."], cancellationToken);
        return result.ExitCode == 0 ? Hosts(result.StandardOutput).ToList() : [];
    }

    [GeneratedRegex(@"https?://(?<host>[A-Za-z0-9.-]+)", RegexOptions.IgnoreCase)]
    private static partial Regex Url();
}
