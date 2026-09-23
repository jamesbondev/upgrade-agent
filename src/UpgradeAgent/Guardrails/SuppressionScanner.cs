using System.Text.RegularExpressions;

namespace UpgradeAgent.Guardrails;

public sealed record Violation(string File, string Rule, string Line);

/// <summary>
/// Looks for ways to make a build or test run pass without fixing anything. Only lines that are
/// genuinely new to a file count: a line removed and re-added (moved or reformatted) is ignored,
/// so pre-existing suppressions don't cause false rejections.
/// </summary>
public static partial class SuppressionScanner
{
    private static readonly (string Rule, Regex Pattern)[] Rules =
    [
        ("#pragma warning disable", PragmaDisable()),
        ("#nullable disable", NullableDisable()),
        ("#if false", IfFalse()),
        ("NoWarn", NoWarn()),
        ("WarningsNotAsErrors", WarningsNotAsErrors()),
        ("TreatWarningsAsErrors=false", TreatWarningsAsErrorsFalse()),
        ("SuppressMessage", SuppressMessage()),
        ("skipped test (Skip =)", SkipAssignment()),
        ("skipped test ([Ignore]/[Explicit])", IgnoreAttribute()),
        ("skipped test (Assert.Skip/Ignore/Inconclusive)", AssertSkip()),
        ("excluded from compilation (<Compile Remove>)", CompileRemove()),
        (".editorconfig severity change", EditorConfigSeverity()),
    ];

    public static IReadOnlyList<Violation> Scan(IEnumerable<FileDiff> diffs)
    {
        var violations = new List<Violation>();
        foreach (var diff in diffs)
        {
            if (diff.IsNew && Path.GetFileName(diff.Path).Equals("GlobalSuppressions.cs", StringComparison.OrdinalIgnoreCase))
            {
                violations.Add(new Violation(diff.Path, "new GlobalSuppressions.cs", ""));
            }

            foreach (var line in GenuinelyAdded(diff))
            {
                foreach (var (rule, pattern) in Rules)
                {
                    if (pattern.IsMatch(line))
                    {
                        violations.Add(new Violation(diff.Path, rule, line.Trim()));
                    }
                }
            }
        }

        return violations;
    }

    internal static IEnumerable<string> GenuinelyAdded(FileDiff diff)
    {
        var removed = diff.Removed
            .Select(l => l.Trim())
            .GroupBy(l => l, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        foreach (var line in diff.Added)
        {
            var key = line.Trim();
            if (removed.TryGetValue(key, out var count) && count > 0)
            {
                removed[key] = count - 1;
                continue;
            }

            yield return line;
        }
    }

    [GeneratedRegex(@"#\s*pragma\s+warning\s+disable", RegexOptions.IgnoreCase)]
    private static partial Regex PragmaDisable();

    [GeneratedRegex(@"#\s*nullable\s+disable", RegexOptions.IgnoreCase)]
    private static partial Regex NullableDisable();

    [GeneratedRegex(@"#\s*if\s+false\b", RegexOptions.IgnoreCase)]
    private static partial Regex IfFalse();

    [GeneratedRegex(@"\bNoWarn\b", RegexOptions.IgnoreCase)]
    private static partial Regex NoWarn();

    [GeneratedRegex(@"\bWarningsNotAsErrors\b", RegexOptions.IgnoreCase)]
    private static partial Regex WarningsNotAsErrors();

    [GeneratedRegex(@"TreatWarningsAsErrors\s*>\s*false", RegexOptions.IgnoreCase)]
    private static partial Regex TreatWarningsAsErrorsFalse();

    [GeneratedRegex(@"\bSuppressMessage\b")]
    private static partial Regex SuppressMessage();

    [GeneratedRegex(@"\bSkip\s*=")]
    private static partial Regex SkipAssignment();

    [GeneratedRegex(@"\[\s*(?:Ignore|Explicit)\b")]
    private static partial Regex IgnoreAttribute();

    [GeneratedRegex(@"\bAssert\.(?:Skip|Ignore|Inconclusive)\b|\bSkip\.(?:If|Unless)\b")]
    private static partial Regex AssertSkip();

    [GeneratedRegex(@"<\s*Compile\s+Remove\b", RegexOptions.IgnoreCase)]
    private static partial Regex CompileRemove();

    [GeneratedRegex(@"dotnet_diagnostic\.[^=]*severity", RegexOptions.IgnoreCase)]
    private static partial Regex EditorConfigSeverity();
}
