using RepoKit;

namespace ReadmeChecker.Detection;

internal static class SignalFinder
{
    public const int MaxIdentifiers = 300;

    private static readonly string[] CodeExtensions = [".cs", ".fs", ".vb", ".ts", ".tsx", ".js", ".py", ".go", ".java", ".kt", ".rb", ".rs"];

    public static async Task<SignalScan> FindAsync(RepoFacts facts, string root, GitCli git, CancellationToken cancellationToken)
    {
        var scan = await IgnoredPaths.DropIgnoredAsync(ReadmeSignals.Find(facts), root, git, cancellationToken);
        var identifiers = await MissingIdentifiersAsync(facts, root, git, cancellationToken);
        return scan with { Signals = SignalScan.Of(scan.Signals.Concat(identifiers)).Signals };
    }

    internal static async Task<IReadOnlyList<Signal>> MissingIdentifiersAsync(RepoFacts facts, string root, GitCli git, CancellationToken cancellationToken)
    {
        if (facts.Readme is not { IsSymlink: false } readme
            || !facts.Files.Any(f => CodeExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)))
        {
            return [];
        }

        var mentions = ReadmeSignals.IdentifierMentions(readme.Text).Take(MaxIdentifiers).ToList();
        if (mentions.Count == 0)
        {
            return [];
        }

        var result = await git.TryRunAsync(
            root,
            ["grep", "-I", "-h", "-o", "-w", "-F", .. mentions.SelectMany(m => new[] { "-e", m.Identifier }), "--", ".", ":(exclude)*.md", ":(exclude)*.markdown"],
            cancellationToken);
        if (result.ExitCode is not (0 or 1))
        {
            return [];
        }

        var found = result.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        return mentions
            .Where(m => !found.Contains(m.Identifier))
            .Select(m => new Signal(SignalKind.MissingIdentifier, m.Line, m.Identifier, "no code or config file in the repository contains this name", m.Identifier))
            .ToList();
    }
}
