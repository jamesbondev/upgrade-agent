using System.Globalization;
using System.Text.RegularExpressions;

namespace UpgradeAgent.Build;

internal enum DiagnosticSeverity
{
    Error,
    Warning,
}

/// <param name="File">The source file, when MSBuild reports one; null for tool-level diagnostics ("CSC : error …").</param>
/// <param name="Origin">The tool that reported a diagnostic without a file (CSC, MSBUILD, NuGet…).</param>
internal sealed record Diagnostic(DiagnosticSeverity Severity, string Code, string Message, string? File, int? Line, string? Origin = null);

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
            var hasLocation = match.Success;
            if (!hasLocation)
            {
                match = WithoutLocation().Match(line);
            }

            if (!match.Success)
            {
                continue;
            }

            // "CSC : error CS2001" names a tool, not a file: only a rooted path or one with an extension is a file.
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

    /// <summary>The error count a <c>dotnet build</c> reported: 0 on success, null when the output doesn't say (e.g. cut by <c>| head</c>).</summary>
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

    /// <summary>The most frequent diagnostic codes, e.g. "CS0117×4, CS1503×2".</summary>
    public static string TopCodes(IEnumerable<Diagnostic> diagnostics, int count) =>
        string.Join(", ", diagnostics.GroupBy(d => d.Code).OrderByDescending(g => g.Count()).Take(count).Select(g => $"{g.Key}×{g.Count()}"));

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

    // Classic logger: "    3 Error(s)"; terminal logger: "Build failed with 3 error(s)".
    [GeneratedRegex(@"(?:^\s*|failed with\s+)(?<count>\d+)\s+Error\(s\)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex ErrorSummary();

    [GeneratedRegex(@"^\s*(?<file>[^\s].*?)\((?<line>\d+)(?:,\d+)*\)\s*:\s*(?<severity>error|warning)\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<message>.*?)(?:\s+\[[^\]]+\])?\s*$")]
    private static partial Regex WithLocation();

    [GeneratedRegex(@"^\s*(?<file>[^\s:][^:]*?)?\s*:\s*(?<severity>error|warning)\s+(?<code>[A-Za-z]+\d+)\s*:\s*(?<message>.*?)(?:\s+\[[^\]]+\])?\s*$")]
    private static partial Regex WithoutLocation();

    [GeneratedRegex(@"(?:Passed|Failed)!\s+-\s+Failed:\s+(?<failed>\d+),\s+Passed:\s+(?<passed>\d+),\s+Skipped:\s+(?<skipped>\d+),\s+Total:\s+(?<total>\d+)")]
    private static partial Regex TestSummary();
}
