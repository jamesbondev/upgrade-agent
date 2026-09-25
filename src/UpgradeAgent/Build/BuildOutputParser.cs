using System.Text.RegularExpressions;

namespace UpgradeAgent.Build;

internal sealed record Diagnostic(string Severity, string Code, string Message, string? File, int? Line);

internal sealed record TestCounts(int Total, int Passed, int Failed, int Skipped);

internal static partial class BuildOutputParser
{
    /// <summary>
    /// Parses MSBuild's canonical error format. Each diagnostic appears once per project that reports it,
    /// so results are de-duplicated.
    /// </summary>
    public static (IReadOnlyList<Diagnostic> Errors, IReadOnlyList<Diagnostic> Warnings) ParseDiagnostics(string output)
    {
        var diagnostics = new List<Diagnostic>();
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var match = WithLocation().Match(line);
            if (!match.Success)
            {
                match = WithoutLocation().Match(line);
            }

            if (!match.Success)
            {
                continue;
            }

            diagnostics.Add(new Diagnostic(
                match.Groups["severity"].Value,
                match.Groups["code"].Value,
                match.Groups["message"].Value.Trim(),
                match.Groups["file"].Value.Trim() is { Length: > 0 } file ? file : null,
                int.TryParse(match.Groups["line"].Value, out var number) ? number : null));
        }

        var distinct = diagnostics.Distinct().ToList();
        return (distinct.Where(d => d.Severity == "error").ToList(), distinct.Where(d => d.Severity == "warning").ToList());
    }

    /// <summary>Sums VSTest's per-assembly summary lines ("Passed!  - Failed: 0, Passed: 16, ...").</summary>
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

    [GeneratedRegex(@"^\s*(?<file>[^\s].*?)\((?<line>\d+)(?:,\d+)*\)\s*:\s*(?<severity>error|warning)\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<message>.*?)(?:\s+\[[^\]]+\])?\s*$")]
    private static partial Regex WithLocation();

    [GeneratedRegex(@"^\s*(?<file>[^\s:][^:]*?)?\s*:\s*(?<severity>error|warning)\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<message>.*?)(?:\s+\[[^\]]+\])?\s*$")]
    private static partial Regex WithoutLocation();

    [GeneratedRegex(@"(?:Passed|Failed)!\s+-\s+Failed:\s+(?<failed>\d+),\s+Passed:\s+(?<passed>\d+),\s+Skipped:\s+(?<skipped>\d+),\s+Total:\s+(?<total>\d+)")]
    private static partial Regex TestSummary();
}
