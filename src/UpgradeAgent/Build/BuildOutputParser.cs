using System.Globalization;
using System.Text.RegularExpressions;

namespace UpgradeAgent.Build;

internal enum DiagnosticSeverity
{
    Error,
    Warning,
}

internal sealed record Diagnostic(DiagnosticSeverity Severity, string Code, string Message, string? File, int? Line, string? Origin = null);

internal sealed record TestCounts(int Total, int Passed, int Failed, int Skipped);

internal static partial class BuildOutputParser
{
    public static (IReadOnlyList<Diagnostic> Errors, IReadOnlyList<Diagnostic> Warnings) ParseDiagnostics(string output)
    {
        var diagnostics = new List<Diagnostic>();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var match = WithLocation().Match(line);
            var hasLocation = match.Success;
            if (!hasLocation)
            {
                match = WithoutLocation().Match(line);
            }

            if (!match.Success)
            {
                continue;
            }

            var prefix = match.Groups["file"].Value.Trim();
            var isFile = hasLocation || Path.IsPathRooted(prefix) || Path.HasExtension(prefix);
            diagnostics.Add(new Diagnostic(
                match.Groups["severity"].Value == "error" ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning,
                match.Groups["code"].Value,
                match.Groups["message"].Value.Trim(),
                prefix.Length > 0 && isFile ? prefix : null,
                int.TryParse(match.Groups["line"].Value, out var number) ? number : null,
                prefix.Length > 0 && !isFile ? prefix : null));
        }

        var distinct = diagnostics.Distinct().ToList();
        return (distinct.Where(d => d.Severity == DiagnosticSeverity.Error).ToList(), distinct.Where(d => d.Severity == DiagnosticSeverity.Warning).ToList());
    }

    public static int? CountErrors(string output)
    {
        if (ErrorSummary().Match(output) is { Success: true } summary)
        {
            return int.Parse(summary.Groups["count"].Value, CultureInfo.InvariantCulture);
        }

        if (output.Contains("Build succeeded", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var (errors, _) = ParseDiagnostics(output);
        return errors.Count > 0 && output.Contains("Build FAILED", StringComparison.OrdinalIgnoreCase)
            ? errors.Count
            : null;
    }

    public static string TopCodes(IEnumerable<Diagnostic> diagnostics, int count) =>
        string.Join(", ", diagnostics.GroupBy(d => d.Code).OrderByDescending(g => g.Count()).Take(count).Select(g => $"{g.Key}×{g.Count()}"));

    public static TestCounts? ParseTestCounts(string output)
    {
        var matches = TestSummary().Matches(output);
        if (matches.Count == 0)
        {
            return null;
        }

        int Sum(string group) => matches.Sum(m => int.Parse(m.Groups[group].Value, System.Globalization.CultureInfo.InvariantCulture));
        return new TestCounts(Sum("total"), Sum("passed"), Sum("failed"), Sum("skipped"));
    }

    [GeneratedRegex(@"(?:^\s*|failed with\s+)(?<count>\d+)\s+Error\(s\)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex ErrorSummary();

    [GeneratedRegex(@"^\s*(?<file>[^\s].*?)\((?<line>\d+)(?:,\d+)*\)\s*:\s*(?<severity>error|warning)\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<message>.*?)(?:\s+\[[^\]]+\])?\s*$")]
    private static partial Regex WithLocation();

    [GeneratedRegex(@"^\s*(?<file>[^\s:][^:]*?)?\s*:\s*(?<severity>error|warning)\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<message>.*?)(?:\s+\[[^\]]+\])?\s*$")]
    private static partial Regex WithoutLocation();

    [GeneratedRegex(@"(?:Passed|Failed)!\s+-\s+Failed:\s+(?<failed>\d+),\s+Passed:\s+(?<passed>\d+),\s+Skipped:\s+(?<skipped>\d+),\s+Total:\s+(?<total>\d+)")]
    private static partial Regex TestSummary();
}
