using RepoKit;

namespace ReadmeChecker.Detection;

internal static class IgnoredPaths
{
    public static async Task<SignalScan> DropIgnoredAsync(SignalScan scan, string root, GitCli git, CancellationToken cancellationToken)
    {
        var targets = scan.Signals
            .Where(s => s.Kind is SignalKind.MissingPath or SignalKind.MissingCommandTarget && s.Target is not null)
            .Select(s => s.Target!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (targets.Count == 0)
        {
            return scan;
        }

        var result = await git.TryRunAsync(root, ["check-ignore", "--no-index", "--", .. targets], cancellationToken);
        var ignored = result.ExitCode is 0 or 1
            ? GitLines(result.StandardOutput).ToHashSet(StringComparer.Ordinal)
            : [];

        return ignored.Count == 0
            ? scan
            : scan with { Signals = scan.Signals.Where(s => s.Target is null || !ignored.Contains(s.Target) || s.Kind == SignalKind.BrokenLink).ToList() };
    }

    private static IEnumerable<string> GitLines(string output) =>
        output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim().Trim('"'));
}
