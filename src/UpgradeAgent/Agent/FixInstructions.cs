using System.Text;
using UpgradeAgent.Build;
using UpgradeAgent.Detection;

namespace UpgradeAgent.Agent;

/// <summary>Builds the agent's instructions and prompts. Pure, so the exact wording is unit-tested and reviewable.</summary>
public static class FixInstructions
{
    private const int MaxErrorsListed = 25;

    /// <summary>Migration notes up to this size are pasted into the task so the model can't skip them.</summary>
    public const int InlineDocBudget = 16_000;

    /// <param name="testArgs">Target:TestArgs, so the agent runs exactly the tests the guardrails compare (e.g. a filter that leaves out Aspire tests).</param>
    public static string System(string solution, TestRunnerMode runnerMode, GroupKind kind, IReadOnlyList<string>? testArgs = null)
    {
        var testCommand = runnerMode == TestRunnerMode.TestingPlatform
            ? $"dotnet test --solution {solution} --no-build"
            : $"dotnet test {solution} --no-build";
        if (testArgs is { Count: > 0 })
        {
            testCommand += " " + string.Join(' ', testArgs.Select(Quote));
        }

        var builder = new StringBuilder($"""
            You are UpgradeAgent's fixer. A deterministic tool has already bumped NuGet package versions in this
            repository and restored packages; the build or the tests now fail. Make the smallest code changes at the
            call sites so that the build and the tests pass, with behaviour unchanged.

            You are in the repository root. Run commands from here, one at a time.
            Build: dotnet build {solution} --no-restore
            Test:  {testCommand}   (after a successful build; use exactly this test scope)

            Automatic checks run after you finish. Breaking any of these rules discards all of your work:
            - Do not change package versions, add packages or restore. Edits to project and build files need a human's approval; avoid them.
            - Do not delete, skip, rename or comment out tests, and do not weaken assertions. Updating a test's calls for a new API signature is fine.
            - Do not suppress diagnostics: no #pragma warning disable, NoWarn, SuppressMessage, .editorconfig severity changes, #nullable disable, #if false, or excluding files from compilation.
            - Do not change TargetFramework, LangVersion or global.json. Do not commit, reset, stash or create branches.
            - Keep behaviour the same. When an API became async, make the callers async and flow a CancellationToken; never block with .Result, .Wait() or .GetAwaiter().GetResult().
            - Migrate to the replacement APIs the migration notes name. Do not re-implement library functionality, inline library code, or change
              constructors and dependencies just to avoid calling the new API.
            - Read the migration notes in the task before changing code. Edits are refused until any listed notes files have been read.
            - If the same error survives three fix attempts, stop working on it and report it as unresolved.
            - Before finishing, build and test once more and make sure both pass.
            """);

        if (kind == GroupKind.PatchMinor)
        {
            builder.AppendLine().Append("""
                - This group has only patch and minor updates. Fix build errors and test failures only. Leave deprecation
                  warnings (such as CS0618) in place unless this repository treats them as errors; report them as upcoming deprecations.
                """);
        }

        return builder.ToString();
    }

    /// <summary>The task prompt, plus the migration files the agent must still read (those too large to inline).</summary>
    public static (string Prompt, IReadOnlyList<string> RequiredReads) Task(
        UpdateGroup group, IReadOnlyList<PackageDocs> docs, BuildResult build, TestRunResult? tests, string worktree, Func<string, string> readFile)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Package group \"{group.Name}\" was updated:");
        foreach (var update in group.Updates)
        {
            builder.AppendLine($"- {update.Id} {update.From} -> {update.To} ({update.Kind.ToString().ToLowerInvariant()})");
        }

        var inlined = new List<(PackageDocs Doc, string File, string Content)>();
        var requiredReads = new List<string>();
        var budget = InlineDocBudget;
        foreach (var doc in docs)
        {
            foreach (var file in doc.MigrationFiles)
            {
                var content = readFile(file);
                if (content.Length <= budget)
                {
                    inlined.Add((doc, file, content));
                    budget -= content.Length;
                }
                else
                {
                    requiredReads.Add(file);
                }
            }
        }

        builder.AppendLine().AppendLine("Migration notes:");
        foreach (var doc in docs)
        {
            var parts = new List<string>();
            var toRead = doc.DocFiles.Where(f => !inlined.Any(i => i.File == f)).ToList();
            if (inlined.Any(i => i.Doc == doc))
            {
                parts.Add("see below");
            }

            if (toRead.Count > 0)
            {
                parts.Add((toRead.Any(requiredReads.Contains) ? "READ FIRST " : "also available: ") + string.Join(", ", toRead));
            }

            if (doc.ReleaseNotes is not null)
            {
                parts.Add($"release notes: \"{Truncate(doc.ReleaseNotes, 400)}\"");
            }

            if ((doc.RepositoryUrl ?? doc.ProjectUrl) is { } url)
            {
                parts.Add($"project: {url}");
            }

            builder.AppendLine($"- {doc.Id} {doc.Version}: {(parts.Count == 0 ? "none found in the package" : string.Join("; ", parts))}");
        }

        foreach (var (doc, file, content) in inlined)
        {
            builder.AppendLine().AppendLine($"=== {Path.GetFileName(file)} from {doc.Id} {doc.Version} ===").AppendLine(content.Trim()).AppendLine("=== end ===");
        }

        builder.AppendLine();
        if (!build.Succeeded)
        {
            builder.AppendLine($"Current build: {build.Errors.Count} error(s).");
            foreach (var error in build.Errors.Take(MaxErrorsListed))
            {
                var file = error.File is null ? "" : Path.GetRelativePath(worktree, error.File).Replace('\\', '/');
                builder.AppendLine($"- {file}{(error.Line is { } line ? $":{line}" : "")} {error.Code}: {error.Message}");
            }

            if (build.Errors.Count > MaxErrorsListed)
            {
                builder.AppendLine($"- … and {build.Errors.Count - MaxErrorsListed} more");
            }
        }
        else if (tests is not null)
        {
            var failed = tests.Inventory?.Failed ?? tests.Counts?.Failed;
            builder.AppendLine($"The build succeeds, but tests fail ({failed?.ToString() ?? "unknown number"} failed). Run the tests to see which.");
        }

        builder.AppendLine().Append("Fix the code, then build and test until both pass. Finish with a short plain-text summary of each change and why.");
        return (builder.ToString(), requiredReads);
    }

    public static string SummaryRequest() => $"""
        Now reply with ONLY a JSON object describing this group, with no prose and no code fence. Do not call any tools.
        Use this shape: {GroupSummaryParser.Schema}
        Use repository-relative paths in "file".
        """;

    /// <summary>Double-quotes arguments with shell-significant characters (filters often contain &amp;, | or !).</summary>
    internal static string Quote(string argument) =>
        argument.Length > 0 && argument.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-' or '/' or ':' or '=' or '~' or ',')
            ? argument
            : $"\"{argument.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static string Truncate(string text, int length)
    {
        var singleLine = text.ReplaceLineEndings(" ");
        return singleLine.Length <= length ? singleLine : singleLine[..(length - 1)] + "…";
    }
}
