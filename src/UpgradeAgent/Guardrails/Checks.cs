using UpgradeAgent.Build;
using UpgradeAgent.Infrastructure;

namespace UpgradeAgent.Guardrails;

internal sealed class GitStateGuardrail : IGuardrail
{
    public const string Name = "Git state";

    public GuardrailCheck Check(GuardrailContext context) =>
        context.Head == context.Input.Start.Commit
            ? new(Name, true, "HEAD unchanged")
            : new(Name, false, $"HEAD moved from {context.Input.Start.Commit.ShortSha()} to {context.Head.ShortSha()}; only the app may commit");
}

internal sealed class BuildGuardrail : IGuardrail
{
    public const string Name = "Build";

    public GuardrailCheck Check(GuardrailContext context) =>
        context.Input.Build.Succeeded
            ? new(Name, true, $"{context.Input.Build.Warnings.Count} warning(s)")
            : new(Name, false, $"{context.Input.Build.Errors.Count} error(s)");
}

internal sealed class TestsGuardrail : IGuardrail
{
    public const string Name = "Tests";

    private const int ShortfallsListed = 3;

    public GuardrailCheck Check(GuardrailContext context) => Evaluate(context.Input.Baseline, context.Input.Tests);

    internal static GuardrailCheck Evaluate(Baseline baseline, TestRunResult? tests)
    {
        if (tests is null)
        {
            return new(Name, false, "not run (build failed)");
        }

        if (!tests.Succeeded)
        {
            return new(Name, false, tests.Failed is > 0 ? $"{tests.Failed} failed" : "dotnet test failed");
        }

        if (baseline.Tests is not null && tests.Inventory is not null)
        {
            var shortfalls = baseline.Tests.Methods
                .Where(m => m.Value.Passed > (tests.Inventory.Methods.TryGetValue(m.Key, out var now) ? now.Passed : 0))
                .Select(m => m.Key)
                .ToList();

            return shortfalls.Count == 0
                ? new(Name, true, $"{tests.Inventory.Passed} passed; every baseline test method still passes")
                : new(Name, false, $"{shortfalls.Count} test method(s) missing or with fewer passing rows: {shortfalls.JoinLimited(ShortfallsListed, ", ")}");
        }

        return tests.Passed switch
        {
            null => new(Name, false, "could not read test results"),
            var passed when passed >= baseline.PassedCount => new(Name, true, $"{passed} passed (count-only check: no TRX, weaker)"),
            var passed => new(Name, false, $"{passed} passed, baseline {baseline.PassedCount} (count-only check)"),
        };
    }
}

internal sealed class SuppressionGuardrail : IGuardrail
{
    public const string Name = "No suppressions or skips";

    private const int ViolationsListed = 5;

    public GuardrailCheck Check(GuardrailContext context)
    {
        var violations = SuppressionScanner.Scan(context.Diffs);
        return violations.Count == 0
            ? new(Name, true, $"{context.Diffs.Count} file(s) changed")
            : new(Name, false, violations.Select(v => $"{v.Rule} in {v.File}").JoinLimited(ViolationsListed));
    }
}

internal sealed class BuildSettingsGuardrail : IGuardrail
{
    public const string Name = "Package versions and build settings";

    private const int ChangesListed = 4;

    public GuardrailCheck Check(GuardrailContext context)
    {
        var added = context.BuildSettings.Except(context.Input.Start.BuildSettings).Select(a => $"+ {a}");
        var removed = context.Input.Start.BuildSettings.Except(context.BuildSettings).Select(r => $"- {r}");
        var changes = removed.Concat(added).ToList();
        return changes.Count == 0
            ? new(Name, true, "unchanged since the bump")
            : new(Name, false, changes.JoinLimited(ChangesListed));
    }
}

internal sealed class FilesGuardrail : IGuardrail
{
    public const string Name = "Files";

    public GuardrailCheck Check(GuardrailContext context)
    {
        var problems = context.Diffs.Where(d => d.IsDeleted && context.IsTestFile(d.Path)).Select(d => $"deleted test file {d.Path}")
            .Concat(context.IgnoredFiles.Except(context.Input.Start.IgnoredFiles).Order(StringComparer.Ordinal).Select(f => $"new git-ignored file {f}"))
            .ToList();
        return problems.Count == 0
            ? new(Name, true, "no test files deleted; no new ignored files")
            : new(Name, false, string.Join("; ", problems));
    }
}
