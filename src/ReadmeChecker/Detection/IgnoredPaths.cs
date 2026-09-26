using RepoKit;

namespace ReadmeChecker.Detection;

internal static class IgnoredPaths
{
    public static async Task<SignalScan> DropIgnoredAsync(SignalScan scan, string root, GitCli git, CancellationToken cancellationToken)
    {
        var targets = scan.Signals
            .Where(CanBeIgnored)
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
            : scan with { Signals = scan.Signals.Where(s => !(CanBeIgnored(s) && ignored.Contains(s.Target!))).ToList() };
    }

    private static bool CanBeIgnored(Signal signal) =>
        signal.Kind is SignalKind.MissingPath or SignalKind.MissingCommandTarget && signal.Target is not null;

    private static IEnumerable<string> GitLines(string output) =>
        output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim().Trim('"'));
}
