using System.Globalization;
using System.Text.RegularExpressions;
using RepoKit;

namespace TestHardener.Analysis;

internal static partial class FixHistory
{
    private const char RecordSeparator = '\u001e';

    public static async Task<IReadOnlyDictionary<string, int>> CountAsync(
        GitCli git, string repoRoot, DateTimeOffset since, CancellationToken cancellationToken)
    {
        var output = await git.RunAsync(
            repoRoot,
            ["log", $"--since={since.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}", "--no-merges", "--format=%x1e%s", "--name-only"],
            cancellationToken);
        return Parse(output);
    }

    internal static IReadOnlyDictionary<string, int> Parse(string gitLogOutput)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var record in gitLogOutput.Split(RecordSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = record.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (lines.Length == 0 || !IsFix(lines[0]))
            {
                continue;
            }

            foreach (var path in lines.Skip(1).Distinct(StringComparer.Ordinal))
            {
                counts[path] = counts.GetValueOrDefault(path) + 1;
            }
        }

        return counts;
    }

    internal static bool IsFix(string subject) => FixSubject().IsMatch(subject);

    [GeneratedRegex(@"^(?:(?:fix|hotfix|bugfix)(?:\([^)]*\))?!?:|Revert\b)", RegexOptions.IgnoreCase)]
    private static partial Regex FixSubject();
}
