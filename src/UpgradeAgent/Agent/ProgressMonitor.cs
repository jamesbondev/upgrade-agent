using System.Globalization;
using System.Text.RegularExpressions;
using UpgradeAgent.Build;

namespace UpgradeAgent.Agent;

/// <summary>
/// Stops an agent that is flailing rather than waiting for the tool-call budget: too many refused actions,
/// or several builds in a row that don't beat the lowest error count so far. A green build resets the count.
/// Limits of 0 disable a check. Thread-safe: permission requests and tool events arrive on different threads.
/// </summary>
internal sealed partial class ProgressMonitor(int maxRefusals, int maxBuildsWithoutProgress, int initialErrors)
{
    private readonly Lock _lock = new();
    private int _refusals;
    private int? _lowestErrors = initialErrors > 0 ? initialErrors : null;
    private int _buildsWithoutProgress;

    /// <summary>Returns a stop reason once the refusal limit is exceeded, otherwise null.</summary>
    public string? RecordRefusal()
    {
        lock (_lock)
        {
            return ++_refusals > maxRefusals && maxRefusals > 0
                ? $"agent stopped: more than {maxRefusals} refused actions"
                : null;
        }
    }

    /// <summary>Records one build's output. Output with no recognisable result (e.g. cut by <c>| head</c>) is ignored.</summary>
    public string? RecordBuild(string output)
    {
        if (CountErrors(output) is not { } errors)
        {
            return null;
        }

        lock (_lock)
        {
            if (errors == 0)
            {
                _lowestErrors = null;
                _buildsWithoutProgress = 0;
                return null;
            }

            if (_lowestErrors is null || errors < _lowestErrors)
            {
                _lowestErrors = errors;
                _buildsWithoutProgress = 0;
                return null;
            }

            return ++_buildsWithoutProgress >= maxBuildsWithoutProgress && maxBuildsWithoutProgress > 0
                ? $"agent stopped: {_buildsWithoutProgress} builds without reducing the errors below {_lowestErrors}"
                : null;
        }
    }

    /// <summary>The error count a <c>dotnet build</c> reported: 0 on success, null when the output doesn't say.</summary>
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

        var (errors, _) = BuildOutputParser.ParseDiagnostics(output);
        return errors.Count > 0 && output.Contains("Build FAILED", StringComparison.OrdinalIgnoreCase)
            ? errors.Count
            : null;
    }

    public static bool IsBuildCommand(string? command) =>
        command is not null && command.Contains("dotnet build", StringComparison.OrdinalIgnoreCase);

    // Classic logger: "    3 Error(s)"; terminal logger: "Build failed with 3 error(s)".
    [GeneratedRegex(@"(?:^\s*|failed with\s+)(?<count>\d+)\s+Error\(s\)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex ErrorSummary();
}
