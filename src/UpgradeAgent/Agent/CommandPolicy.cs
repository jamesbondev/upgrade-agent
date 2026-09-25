using AgentHarness;
using AgentHarness.Policies;

namespace UpgradeAgent.Agent;

internal sealed class CommandPolicy : IToolPolicy
{
    private const string NoNetwork =
        "No network access. The migration notes are in the task and the NuGet packages folder. "
        + "If the replacement API isn't documented there or visible in the code, report the error as unresolved.";

    private static readonly HashSet<string> DotnetFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "--no-restore", "--no-build", "--no-incremental", "-nologo", "--nologo", "-tl:off", "--tl:off", "--list-tests", "--blame", "--blame-hang",
    };

    private static readonly HashSet<string> DotnetFlagsWithValue = new(StringComparer.OrdinalIgnoreCase)
    {
        "-v", "--verbosity", "-c", "--configuration", "-f", "--framework", "--filter", "--logger", "--blame-hang-timeout", "--results-directory",
    };

    private readonly WorkspacePolicy _workspace;

    public CommandPolicy(string worktree, IReadOnlyList<string> readOnlyRoots)
    {
        var options = new WorkspacePolicyOptions
        {
            AutoApprovedEditExtensions = { ".cs", ".fs", ".vb", ".razor", ".cshtml" },
            NetworkRefusal = NoNetwork,
            GitRefusal = "Only read-only git commands (status, diff, log, show) are allowed; UpgradeAgent owns commits and branches.",
            EditAskReason = path => $"edit to a non-source file: {path}",
            SensitiveRefusal = name => $"'{name}' can hold credentials (such as NuGet feed passwords) and isn't needed to fix code.",
            UnknownCommandRefusal = command =>
                $"'{command}' is not available. Use dotnet build/test, read-only commands (cat, grep, find, ls, git diff) and the edit tool.",
        };
        options.Commands["dotnet"] = EvaluateDotnet;
        foreach (var root in readOnlyRoots)
        {
            options.ReadOnlyRoots.Add(root);
        }

        _workspace = new WorkspacePolicy(worktree, options);
    }

    public ValueTask<ToolDecision> EvaluateAsync(ToolRequest request, CancellationToken cancellationToken) => _workspace.EvaluateAsync(request, cancellationToken);

    public ToolDecision EvaluateShell(string commandLine, bool hasWriteFileRedirection, IReadOnlyList<string>? possiblePaths = null) =>
        _workspace.EvaluateShell(commandLine, hasWriteFileRedirection, possiblePaths);

    public ToolDecision EvaluateWrite(string path) => _workspace.EvaluateWrite(path);

    public ToolDecision EvaluateRead(string path) => _workspace.EvaluateRead(path);

    private static ToolDecision EvaluateDotnet(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
        {
            return ToolDecision.Reject("Use dotnet build or dotnet test.");
        }

        var verb = arguments[0].ToLowerInvariant();
        switch (verb)
        {
            case "--version" or "--info" or "--list-sdks":
                return ToolDecision.Approve($"dotnet {verb}");
            case "restore":
                return ToolDecision.Reject("Packages are already restored by UpgradeAgent. Use dotnet build --no-restore.");
            case "add" or "remove" or "package" or "nuget" or "list":
                return ToolDecision.Reject("Package references and versions are managed by UpgradeAgent; don't change them.");
            case not ("build" or "test"):
                return ToolDecision.Reject($"dotnet {verb} is not available. Use dotnet build --no-restore and dotnet test --no-build.");
        }

        var rest = arguments.Skip(1).ToList();
        if (verb == "build" && !rest.Contains("--no-restore", StringComparer.OrdinalIgnoreCase))
        {
            return ToolDecision.Reject("Add --no-restore: packages are already restored.");
        }

        if (verb == "test" && !rest.Any(a => a.Equals("--no-restore", StringComparison.OrdinalIgnoreCase) || a.Equals("--no-build", StringComparison.OrdinalIgnoreCase)))
        {
            return ToolDecision.Reject("Add --no-build (after a successful dotnet build --no-restore) or --no-restore.");
        }

        for (var i = 0; i < rest.Count; i++)
        {
            var argument = rest[i];
            if (argument.StartsWith("-p:", StringComparison.OrdinalIgnoreCase) || argument.StartsWith("/p:", StringComparison.OrdinalIgnoreCase)
                || argument.StartsWith("--property", StringComparison.OrdinalIgnoreCase))
            {
                return ToolDecision.Reject("MSBuild property overrides are not allowed; build with the repository's own settings.");
            }

            if (DotnetFlagsWithValue.Contains(argument))
            {
                i++;
                continue;
            }

            if (DotnetFlags.Contains(argument) || argument.StartsWith("-v:", StringComparison.OrdinalIgnoreCase)
                || argument.StartsWith("-clp:", StringComparison.OrdinalIgnoreCase) || argument.StartsWith("--filter=", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (argument.StartsWith('-'))
            {
                return ToolDecision.Reject($"The option {argument} is not allowed. Use the build and test commands given in the instructions.");
            }
        }

        return ToolDecision.Approve($"dotnet {verb}");
    }
}
