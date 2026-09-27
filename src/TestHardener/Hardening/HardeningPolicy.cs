using AgentHarness;
using AgentHarness.Policies;

namespace TestHardener.Hardening;

internal static class HardeningPolicy
{
    public const string OtherToolRefusal = "Only reading, read-only commands, dotnet build/test on the test projects, and editing the one test file are available.";

    private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase)
    {
        "--no-restore", "--no-build", "-nologo", "--nologo", "-tl:off", "--tl:off", "--list-tests",
    };

    private static readonly HashSet<string> FlagsWithValue = new(StringComparer.OrdinalIgnoreCase)
    {
        "-v", "--verbosity", "-c", "--configuration", "-f", "--framework", "--filter",
    };

    public static IToolPolicy Create(string root, string ownedPath, IReadOnlyList<string> testProjects, Func<string?>? checkBeforeTests = null)
    {
        var owned = Path.GetFullPath(Path.Combine(root, ownedPath));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var options = new WorkspacePolicyOptions
        {
            NetworkRefusal = "No network access. Everything you need is in the repository.",
            GitRefusal = "Only read-only git commands (status, diff, log, show, ls-files, grep, blame) are allowed; TestHardener owns commits.",
            UnknownCommandRefusal = command =>
                $"'{command}' is not available. Use read-only commands (cat, grep, find, ls, git diff), dotnet build/test on a test project, and the edit tool.",
        };
        options.Commands["dotnet"] = arguments => EvaluateDotnet(arguments, testProjects, root, checkBeforeTests);

        return new WorkspacePolicy(root, options).Wrap((request, decision) => request switch
        {
            ShellRequest when decision.Verdict == ToolVerdict.Reject => decision with { CountsTowardRefusalLimit = false },
            FileReadRequest or ShellRequest => decision,
            FileWriteRequest write when Path.GetFullPath(write.Path, root).Equals(owned, comparison) => ToolDecision.Approve("the test file this task owns"),
            FileWriteRequest => ToolDecision.Reject(
                $"Only {ownedPath} may be changed. If a test needs a builder or helper somewhere else, don't write it; say so in your summary."),
            _ => ToolDecision.Reject(OtherToolRefusal),
        });
    }

    internal static ToolDecision EvaluateDotnet(IReadOnlyList<string> arguments, IReadOnlyList<string> testProjects, string root, Func<string?>? checkBeforeTests = null)
    {
        var names = string.Join(", ", testProjects);
        if (arguments.Count == 0 || arguments[0].ToLowerInvariant() is not ("build" or "test"))
        {
            return ToolDecision.Reject($"Only dotnet build and dotnet test are available, on a test project: {names}.");
        }

        var verb = arguments[0].ToLowerInvariant();
        var rest = arguments.Skip(1).ToList();
        string? project = null;
        for (var i = 0; i < rest.Count; i++)
        {
            var argument = rest[i];
            if (argument.StartsWith("-p:", StringComparison.OrdinalIgnoreCase) || argument.StartsWith("/p:", StringComparison.OrdinalIgnoreCase)
                || argument.StartsWith("--property", StringComparison.OrdinalIgnoreCase))
            {
                return ToolDecision.Reject("MSBuild property overrides aren't allowed; build with the repository's own settings.");
            }

            if (FlagsWithValue.Contains(argument))
            {
                i++;
            }
            else if (Flags.Contains(argument) || argument.StartsWith("-v:", StringComparison.OrdinalIgnoreCase)
                || argument.StartsWith("--filter=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            else if (argument.StartsWith('-'))
            {
                return ToolDecision.Reject($"The option {argument} isn't allowed. TestHardener runs the tests with logging itself; use --filter to pick tests.");
            }
            else if (project is null)
            {
                project = argument;
            }
            else
            {
                return ToolDecision.Reject("Name one test project only.");
            }
        }

        if (project is not null && !InsideRepo(project, root))
        {
            return ToolDecision.Reject($"{project} is outside the repository. Name one of: {names}.");
        }

        if (project is null || !testProjects.Any(p => Matches(project, p)))
        {
            return ToolDecision.Reject($"Name the test project to {verb}, one of: {names}. A bare dotnet {verb} would build the whole solution, including slow suites.");
        }

        if (!rest.Any(a => a.Equals("--no-restore", StringComparison.OrdinalIgnoreCase) || a.Equals("--no-build", StringComparison.OrdinalIgnoreCase)))
        {
            return ToolDecision.Reject(verb == "build"
                ? "Add --no-restore: packages are already restored."
                : "Add --no-build (after dotnet build --no-restore) or --no-restore.");
        }

        if (verb == "test" && checkBeforeTests?.Invoke() is { } problems)
        {
            return ToolDecision.Reject($"The test file doesn't pass TestHardener's checks yet, so its tests can't run. Fix these first:{Environment.NewLine}{problems}");
        }

        return ToolDecision.Approve($"dotnet {verb} {project}");
    }

    private static bool InsideRepo(string argument, string root)
    {
        var normalized = argument.Replace('\\', '/');
        if (normalized.Split('/').Contains("..", StringComparer.Ordinal))
        {
            return false;
        }

        if (!Path.IsPathRooted(argument))
        {
            return true;
        }

        var full = Path.GetFullPath(argument);
        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(rootFull, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    private static bool Matches(string argument, string testProject)
    {
        var trimmed = argument.Replace('\\', '/').TrimEnd('/');
        var name = Path.GetFileName(trimmed);
        var projectFile = Path.GetFileName(testProject);
        var projectFolder = Path.GetFileName(Path.GetDirectoryName(testProject) ?? "");
        return name.Equals(projectFile, StringComparison.OrdinalIgnoreCase) || (name.Length > 0 && name.Equals(projectFolder, StringComparison.OrdinalIgnoreCase));
    }
}
