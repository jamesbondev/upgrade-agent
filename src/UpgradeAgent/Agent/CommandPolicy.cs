using UpgradeAgent.Infrastructure;

namespace UpgradeAgent.Agent;

internal enum PolicyVerdict
{
    Approve,
    AskOperator,
    Reject,
}

/// <param name="Reason">For a rejection, this goes back to the agent as feedback, so it says what to do instead.</param>
internal sealed record PolicyDecision(PolicyVerdict Verdict, string Reason)
{
    public static PolicyDecision Approve(string reason) => new(PolicyVerdict.Approve, reason);

    public static PolicyDecision Ask(string reason) => new(PolicyVerdict.AskOperator, reason);

    public static PolicyDecision Reject(string reason) => new(PolicyVerdict.Reject, reason);
}

/// <summary>
/// Decides what the agent may do without asking. Builds, tests, reads and source edits inside the
/// working copy are automatic; edits to non-source files (project and build files) go to the operator;
/// everything else is refused with feedback saying what to do instead, so unattended runs never stall
/// on a prompt the agent had no real need for.
/// This is a usability layer on a non-sandboxed shell, not a security boundary: the deterministic
/// guardrails after the agent finishes are what decide whether its work is kept.
/// </summary>
internal sealed class CommandPolicy
{
    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase) { ".cs", ".fs", ".vb", ".razor", ".cshtml" };

    private static readonly HashSet<string> ReadOnlyCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "ls", "cat", "head", "tail", "grep", "egrep", "rg", "wc", "pwd", "echo", "sort", "uniq", "tree", "file", "diff", "stat", "basename", "dirname", "realpath", "true",
        "dir", "type", "Get-ChildItem", "gci", "Get-Content", "gc", "Select-String", "sls", "Get-Location", "Measure-Object", "Sort-Object", "Select-Object", "Format-Table", "Out-String",
    };

    private static readonly HashSet<string> NetworkCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "curl", "wget", "Invoke-WebRequest", "iwr", "Invoke-RestMethod", "irm", "nuget", "ssh", "scp", "ftp", "nc",
    };

    private const string NoNetwork =
        "No network access. The migration notes are in the task and the NuGet packages folder. "
        + "If the replacement API isn't documented there or visible in the code, report the error as unresolved.";

    private static readonly HashSet<string> ReadOnlyGit = new(StringComparer.OrdinalIgnoreCase) { "status", "diff", "log", "show", "ls-files", "grep", "blame" };

    private static readonly HashSet<string> FindWriteActions = new(StringComparer.Ordinal) { "-exec", "-execdir", "-ok", "-okdir", "-delete", "-fprint", "-fprint0", "-fprintf", "-fls" };

    private static readonly HashSet<string> DotnetFlags = new(StringComparer.OrdinalIgnoreCase)
    {
        "--no-restore", "--no-build", "--no-incremental", "-nologo", "--nologo", "-tl:off", "--tl:off", "--list-tests", "--blame", "--blame-hang",
    };

    private static readonly HashSet<string> DotnetFlagsWithValue = new(StringComparer.OrdinalIgnoreCase)
    {
        "-v", "--verbosity", "-c", "--configuration", "-f", "--framework", "--filter", "--logger", "--blame-hang-timeout", "--results-directory",
    };

    /// <summary>Files that can hold credentials (feed passwords, keys) and are never needed to fix code.</summary>
    private static readonly HashSet<string> SensitiveNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "nuget.config", ".env", "secrets.json", ".git-credentials", ".npmrc", ".pypirc", "credentials", "credentials.json",
    };

    private static readonly HashSet<string> SensitiveExtensions = new(StringComparer.OrdinalIgnoreCase) { ".pfx", ".p12", ".snk", ".pem", ".key" };

    private readonly string _worktree;
    private readonly IReadOnlyList<string> _readOnlyRoots;

    /// <param name="readOnlyRoots">Extra folders the agent may read, e.g. the NuGet global packages folder for migration notes.</param>
    public CommandPolicy(string worktree, IReadOnlyList<string> readOnlyRoots)
    {
        _worktree = Normalize(worktree);
        _readOnlyRoots = readOnlyRoots.Select(Normalize).ToList();
    }

    public PolicyDecision EvaluateShell(string commandLine, bool hasWriteFileRedirection, IReadOnlyList<string>? possiblePaths = null)
    {
        if (hasWriteFileRedirection)
        {
            return PolicyDecision.Reject("Writing files with shell redirection is not allowed. Edit files with the edit tool; read command output directly.");
        }

        var parsed = ShellCommandParser.Parse(commandLine, out var error);
        if (error is not null)
        {
            return PolicyDecision.Reject($"Command not allowed: {error}. Run one simple command at a time from the repository root.");
        }

        foreach (var path in possiblePaths ?? [])
        {
            if (IsSensitive(path))
            {
                return SensitiveRefusal(path);
            }

            if (!IsInside(Resolve(path, _worktree), [_worktree, .. _readOnlyRoots]))
            {
                return PolicyDecision.Reject($"'{path}' is outside the working copy.");
            }
        }

        var cwd = _worktree;
        var decisions = new List<PolicyDecision>();
        foreach (var segment in parsed.Segments)
        {
            var decision = EvaluateSegment(segment, ref cwd);
            if (decision.Verdict == PolicyVerdict.Reject)
            {
                return decision;
            }

            decisions.Add(decision);
        }

        return decisions.FirstOrDefault(d => d.Verdict == PolicyVerdict.AskOperator)
            ?? PolicyDecision.Approve(decisions.Count == 1 ? decisions[0].Reason : "all segments auto-approved");
    }

    public PolicyDecision EvaluateWrite(string path)
    {
        if (IsSensitive(path))
        {
            return SensitiveRefusal(path);
        }

        var full = Resolve(path, _worktree);
        if (!IsInside(full, [_worktree]))
        {
            return PolicyDecision.Reject($"'{path}' is outside the working copy; only files in the repository may be edited.");
        }

        var relative = RepoPath.Relative(_worktree, full);
        if (relative == ".git" || relative.StartsWith(".git/", StringComparison.Ordinal))
        {
            return PolicyDecision.Reject("The .git folder must not be edited.");
        }

        return SourceExtensions.Contains(Path.GetExtension(full))
            ? PolicyDecision.Approve($"source edit: {relative}")
            : PolicyDecision.Ask($"edit to a non-source file: {relative}");
    }

    public PolicyDecision EvaluateRead(string path) =>
        IsSensitive(path) ? SensitiveRefusal(path)
        : IsInside(Resolve(path, _worktree), [_worktree, .. _readOnlyRoots])
            ? PolicyDecision.Approve("read inside the working copy or package docs")
            : PolicyDecision.Reject($"'{path}' is outside the working copy and package folders.");

    private PolicyDecision EvaluateSegment(IReadOnlyList<string> tokens, ref string cwd)
    {
        var command = tokens[0];
        var arguments = tokens.Skip(1).ToList();

        if (arguments.FirstOrDefault(a => !a.StartsWith('-') && IsSensitive(a)) is { } sensitive)
        {
            return SensitiveRefusal(sensitive);
        }

        foreach (var argument in arguments.Where(LooksLikeEscapingPath))
        {
            if (!IsInside(Resolve(argument, cwd), [_worktree, .. _readOnlyRoots]))
            {
                return PolicyDecision.Reject($"'{argument}' is outside the working copy.");
            }
        }

        switch (command.ToLowerInvariant())
        {
            case "cd" or "set-location" or "pushd":
                if (arguments.Count != 1)
                {
                    return PolicyDecision.Reject("cd takes exactly one folder.");
                }

                var target = Resolve(arguments[0], cwd);
                if (!IsInside(target, [_worktree]))
                {
                    return PolicyDecision.Reject($"'{arguments[0]}' is outside the working copy.");
                }

                cwd = target;
                return PolicyDecision.Approve("cd inside the working copy");

            case "dotnet":
                return EvaluateDotnet(arguments);

            case "git":
                return arguments.Count > 0 && ReadOnlyGit.Contains(arguments[0])
                    ? PolicyDecision.Approve($"read-only git {arguments[0]}")
                    : PolicyDecision.Reject("Only read-only git commands (status, diff, log, show) are allowed; UpgradeAgent owns commits and branches.");

            case "find":
                return arguments.Any(FindWriteActions.Contains)
                    ? PolicyDecision.Reject("find actions that run commands or delete files are not allowed.")
                    : PolicyDecision.Approve("read-only find");

            case "sed":
                return arguments.Any(a => a == "-i" || a.StartsWith("-i", StringComparison.Ordinal) || a == "--in-place")
                    ? PolicyDecision.Reject("Edit files with the edit tool, not sed -i.")
                    : PolicyDecision.Approve("read-only sed");

            case "rm" or "del" or "remove-item" or "mv" or "move-item" or "cp" or "copy-item":
                return PolicyDecision.Reject($"'{command}' is not allowed. Change code with the edit tool; leave files where they are.");

            default:
                return ReadOnlyCommands.Contains(command) ? PolicyDecision.Approve($"read-only {command}")
                    : NetworkCommands.Contains(command) ? PolicyDecision.Reject(NoNetwork)
                    : PolicyDecision.Reject($"'{command}' is not available. Use dotnet build/test, read-only commands (cat, grep, find, ls, git diff) and the edit tool.");
        }
    }

    private static PolicyDecision EvaluateDotnet(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
        {
            return PolicyDecision.Reject("Use dotnet build or dotnet test.");
        }

        var verb = arguments[0].ToLowerInvariant();
        switch (verb)
        {
            case "--version" or "--info" or "--list-sdks":
                return PolicyDecision.Approve($"dotnet {verb}");
            case "restore":
                return PolicyDecision.Reject("Packages are already restored by UpgradeAgent. Use dotnet build --no-restore.");
            case "add" or "remove" or "package" or "nuget" or "list":
                return PolicyDecision.Reject("Package references and versions are managed by UpgradeAgent; don't change them.");
            case not ("build" or "test"):
                return PolicyDecision.Reject($"dotnet {verb} is not available. Use dotnet build --no-restore and dotnet test --no-build.");
        }

        var rest = arguments.Skip(1).ToList();
        if (verb == "build" && !rest.Contains("--no-restore", StringComparer.OrdinalIgnoreCase))
        {
            return PolicyDecision.Reject("Add --no-restore: packages are already restored.");
        }

        if (verb == "test" && !rest.Any(a => a is "--no-restore" or "--no-build"))
        {
            return PolicyDecision.Reject("Add --no-build (after a successful dotnet build --no-restore) or --no-restore.");
        }

        for (var i = 0; i < rest.Count; i++)
        {
            var argument = rest[i];
            if (argument.StartsWith("-p:", StringComparison.OrdinalIgnoreCase) || argument.StartsWith("/p:", StringComparison.OrdinalIgnoreCase)
                || argument.StartsWith("--property", StringComparison.OrdinalIgnoreCase))
            {
                return PolicyDecision.Reject("MSBuild property overrides are not allowed; build with the repository's own settings.");
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
                return PolicyDecision.Reject($"The option {argument} is not allowed. Use the build and test commands given in the instructions.");
            }
        }

        return PolicyDecision.Approve($"dotnet {verb}");
    }

    public static bool IsSensitive(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('/', '\\'));
        return SensitiveNames.Contains(name) || SensitiveExtensions.Contains(Path.GetExtension(name));
    }

    private static PolicyDecision SensitiveRefusal(string path) =>
        PolicyDecision.Reject($"'{Path.GetFileName(path)}' can hold credentials (such as NuGet feed passwords) and isn't needed to fix code.");

    /// <summary>Relative paths without ".." stay inside the current folder, which is always inside the working copy.</summary>
    private static bool LooksLikeEscapingPath(string token) =>
        !token.StartsWith('-')
        && (Path.IsPathRooted(token) || token.StartsWith('~') || token.Split('/', '\\').Contains(".."));

    private static string Resolve(string path, string cwd)
    {
        var expanded = path.StartsWith('~')
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + path[1..]
            : path;
        return Normalize(Path.GetFullPath(expanded, cwd));
    }

    private static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsInside(string path, IEnumerable<string> roots)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return roots.Any(root =>
            path.Equals(root, comparison)
            || path.StartsWith(root + Path.DirectorySeparatorChar, comparison));
    }
}
