using System.Text.RegularExpressions;

namespace AgentHarness.Policies;

/// <summary>A rule for one program (the first word of a shell command). It gets the words after the program name.</summary>
public delegate ToolDecision CommandRule(IReadOnlyList<string> arguments);

/// <summary>Ready-made <see cref="CommandRule"/>s.</summary>
public static class CommandRules
{
    public static CommandRule Approve(string reason = "allowed command") => _ => ToolDecision.Approve(reason);

    public static CommandRule Ask(string reason) => _ => ToolDecision.Ask(reason);

    public static CommandRule Reject(string feedback) => _ => ToolDecision.Reject(feedback);

    /// <summary>Approves only the listed sub-commands, e.g. <c>ApproveVerbs("build", "test")</c> for <c>dotnet</c>.</summary>
    public static CommandRule ApproveVerbs(params string[] verbs) => arguments =>
        arguments.Count > 0 && verbs.Contains(arguments[0], StringComparer.OrdinalIgnoreCase)
            ? ToolDecision.Approve($"allowed: {arguments[0]}")
            : ToolDecision.Reject($"Only these sub-commands are available: {string.Join(", ", verbs)}.");
}

/// <summary>What <see cref="WorkspacePolicy"/> allows. The defaults are safe for an unattended coding agent.</summary>
public sealed class WorkspacePolicyOptions
{
    /// <summary>Folders outside the workspace the agent may read (never write), e.g. a package cache with docs.</summary>
    public IList<string> ReadOnlyRoots { get; } = [];

    /// <summary>Edits to files with these extensions (".cs" or "cs") run on their own; other edits in the workspace go to the operator.</summary>
    public ISet<string> AutoApprovedEditExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Programs that only read. Approved when every path they name stays inside the allowed folders.</summary>
    public ISet<string> ReadOnlyCommands { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "ls", "cat", "head", "tail", "grep", "egrep", "rg", "wc", "pwd", "echo", "sort", "uniq", "tree", "file", "diff", "stat", "basename", "dirname", "realpath", "true",
        "dir", "type", "Get-ChildItem", "gci", "Get-Content", "gc", "Select-String", "sls", "Get-Location", "Measure-Object", "Sort-Object", "Select-Object", "Format-Table", "Out-String",
    };

    /// <summary>Programs that reach the network. Refused with <see cref="NetworkRefusal"/>.</summary>
    public ISet<string> NetworkCommands { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "curl", "wget", "Invoke-WebRequest", "iwr", "Invoke-RestMethod", "irm", "nuget", "ssh", "scp", "ftp", "nc",
    };

    /// <summary>Your rules for other programs, by name: <c>Commands["dotnet"] = CommandRules.ApproveVerbs("build", "test")</c>.</summary>
    public IDictionary<string, CommandRule> Commands { get; } = new Dictionary<string, CommandRule>(StringComparer.OrdinalIgnoreCase);

    /// <summary>git sub-commands that only read.</summary>
    public ISet<string> ReadOnlyGitCommands { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "status", "diff", "log", "show", "ls-files", "grep", "blame" };

    /// <summary>Files that can hold credentials. Never read or written, whatever the tool.</summary>
    public ISet<string> SensitiveFileNames { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "nuget.config", ".env", "secrets.json", ".git-credentials", ".npmrc", ".pypirc", "credentials", "credentials.json",
    };

    public ISet<string> SensitiveExtensions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".pfx", ".p12", ".snk", ".pem", ".key" };

    /// <summary>What the operator is told about an edit outside <see cref="AutoApprovedEditExtensions"/>, given its workspace-relative path.</summary>
    public Func<string, string> EditAskReason { get; set; } = _ => "this file type isn't edited without your approval";

    public string NetworkRefusal { get; set; } = "No network access in this session. Work from the files in the workspace.";

    public string GitRefusal { get; set; } = "Only read-only git commands (status, diff, log, show) are allowed; the app owns commits and branches.";

    /// <summary>Feedback for a sensitive file, given its name.</summary>
    public Func<string, string> SensitiveRefusal { get; set; } = name => $"'{name}' can hold credentials and isn't needed for this task.";

    /// <summary>Feedback for a program no rule covers, given its name. Say what the agent can use instead.</summary>
    public Func<string, string> UnknownCommandRefusal { get; set; } = command =>
        $"'{command}' is not available. Use read-only commands (cat, grep, find, ls, git diff) and the edit tool.";
}

/// <summary>
/// A sensible default policy for a coding agent confined to one folder:
/// <list type="bullet">
/// <item>Reads inside the workspace (and <see cref="WorkspacePolicyOptions.ReadOnlyRoots"/>) run on their own.</item>
/// <item>Edits run on their own for <see cref="WorkspacePolicyOptions.AutoApprovedEditExtensions"/>, go to the operator
/// for other files in the workspace, and are refused outside it and in <c>.git</c>.</item>
/// <item>Shell commands are parsed (<see cref="ShellCommandParser"/>); each part must be a read-only command, a read-only
/// git command, <c>cd</c> inside the workspace, or a program with a rule in <see cref="WorkspacePolicyOptions.Commands"/>.
/// Everything else is refused with feedback that says what to do instead, so unattended runs never stall on a prompt.</item>
/// <item>Credential files are refused everywhere.</item>
/// </list>
/// This is a usability layer on a non-sandboxed shell, not a security boundary: check the agent's work
/// yourself afterwards (build, tests, diff review), and run it where a mistake is cheap.
/// </summary>
public sealed partial class WorkspacePolicy : IToolPolicy
{
    private static readonly string[] GitRunsPrograms = ["--ext-diff", "--textconv", "--open-files-in-pager"];

    private static readonly HashSet<string> FindWriteActions = new(StringComparer.Ordinal) { "-exec", "-execdir", "-ok", "-okdir", "-delete", "-fprint", "-fprint0", "-fprintf", "-fls" };

    private readonly string _root;
    private readonly IReadOnlyList<string> _readOnlyRoots;
    private readonly WorkspacePolicyOptions _options;

    public WorkspacePolicy(string root, WorkspacePolicyOptions? options = null)
    {
        _options = options ?? new WorkspacePolicyOptions();
        _root = Normalize(root);
        _readOnlyRoots = _options.ReadOnlyRoots.Select(Normalize).ToList();
    }

    /// <summary>Creates a policy and lets you adjust its options inline.</summary>
    public WorkspacePolicy(string root, Action<WorkspacePolicyOptions> configure)
        : this(root, Configure(configure))
    {
    }

    public string Root => _root;

    public ValueTask<ToolDecision> EvaluateAsync(ToolRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(request switch
    {
        ShellRequest shell => EvaluateShell(shell.CommandLine, shell.WritesFile, shell.PossiblePaths),
        FileWriteRequest write => EvaluateWrite(write.Path),
        FileReadRequest read => EvaluateRead(read.Path),
        WebFetchRequest => ToolDecision.Ask("fetch a web page (its content is untrusted)"),
        CustomToolRequest custom => ToolDecision.Ask($"{custom.Name} needs your approval"),
        McpToolRequest mcp => ToolDecision.Ask($"MCP tool {mcp.Tool} on {mcp.Server}{(mcp.ReadOnly ? " (read-only)" : "")}"),
        _ => ToolDecision.Reject($"'{request.Describe()}' is not available in this session."),
    });

    public ToolDecision EvaluateShell(string commandLine, bool hasWriteFileRedirection = false, IReadOnlyList<string>? possiblePaths = null)
    {
        if (hasWriteFileRedirection)
        {
            return ToolDecision.Reject("Writing files with shell redirection is not allowed. Edit files with the edit tool; read command output directly.");
        }

        if (!ShellCommandParser.TryParse(commandLine, out var parsed, out var error))
        {
            return ToolDecision.Reject($"Command not allowed: {error}. Run one simple command at a time from the workspace root.");
        }

        foreach (var path in possiblePaths ?? [])
        {
            if (IsSensitive(path))
            {
                return Sensitive(path);
            }

            if (!IsInside(Resolve(path, _root), [_root, .. _readOnlyRoots]))
            {
                return ToolDecision.Reject($"'{path}' is outside the working copy.");
            }
        }

        if (parsed.Segments.Count == 0)
        {
            return ToolDecision.Reject("Empty command.");
        }

        var cwd = _root;
        var decisions = new List<ToolDecision>();
        foreach (var segment in parsed.Segments)
        {
            var decision = EvaluateSegment(segment, ref cwd);
            if (decision.Verdict == ToolVerdict.Reject)
            {
                return decision;
            }

            decisions.Add(decision);
        }

        return decisions.FirstOrDefault(d => d.Verdict == ToolVerdict.Ask)
            ?? ToolDecision.Approve(decisions.Count == 1 ? decisions[0].Reason : "all segments auto-approved");
    }

    public ToolDecision EvaluateWrite(string path)
    {
        if (IsSensitive(path))
        {
            return Sensitive(path);
        }

        var full = Resolve(path, _root);
        if (!IsInside(full, [_root]))
        {
            return ToolDecision.Reject($"'{path}' is outside the working copy; only files in the workspace may be edited.");
        }

        var relative = Path.GetRelativePath(_root, full).Replace('\\', '/');
        if (relative == ".git" || relative.StartsWith(".git/", StringComparison.Ordinal))
        {
            return ToolDecision.Reject("The .git folder must not be edited.");
        }

        var extension = Path.GetExtension(full);
        return _options.AutoApprovedEditExtensions.Contains(extension) || (extension.Length > 1 && _options.AutoApprovedEditExtensions.Contains(extension[1..]))
            ? ToolDecision.Approve($"source edit: {relative}")
            : ToolDecision.Ask(_options.EditAskReason(relative));
    }

    public ToolDecision EvaluateRead(string path) =>
        IsSensitive(path) ? Sensitive(path)
        : IsInside(Resolve(path, _root), [_root, .. _readOnlyRoots])
            ? ToolDecision.Approve("read inside the allowed folders")
            : ToolDecision.Reject($"'{path}' is outside the working copy and the folders it may read.");

    /// <summary>True for files that can hold credentials (<see cref="WorkspacePolicyOptions.SensitiveFileNames"/> and extensions).</summary>
    public bool IsSensitive(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('/', '\\'));
        return _options.SensitiveFileNames.Contains(name) || _options.SensitiveExtensions.Contains(Path.GetExtension(name));
    }

    private ToolDecision EvaluateSegment(IReadOnlyList<string> tokens, ref string cwd)
    {
        var command = tokens[0];
        var arguments = tokens.Skip(1).ToList();

        // Paths hide in option values (--from-file=/etc/passwd) and git revisions (HEAD:nuget.config).
        var paths = arguments.Select(PathIn).OfType<string>().ToList();
        if (paths.SelectMany(p => new[] { p, AfterRevision(p) }).FirstOrDefault(IsSensitive) is { } sensitive)
        {
            return Sensitive(sensitive);
        }

        foreach (var argument in paths.Where(LooksLikeEscapingPath))
        {
            if (!IsInside(Resolve(argument, cwd), [_root, .. _readOnlyRoots]))
            {
                return ToolDecision.Reject($"'{argument}' is outside the working copy.");
            }
        }

        // The app's own rules come first, so it can allow (or narrow) anything below.
        if (_options.Commands.TryGetValue(command, out var rule))
        {
            return rule(arguments);
        }

        switch (command.ToLowerInvariant())
        {
            case "cd" or "set-location" or "pushd":
                if (arguments.Count != 1)
                {
                    return ToolDecision.Reject("cd takes exactly one folder.");
                }

                var target = Resolve(arguments[0], cwd);
                if (!IsInside(target, [_root]))
                {
                    return ToolDecision.Reject($"'{arguments[0]}' is outside the working copy.");
                }

                cwd = target;
                return ToolDecision.Approve("cd inside the working copy");

            case "git":
                if (arguments.Count == 0 || !_options.ReadOnlyGitCommands.Contains(arguments[0]))
                {
                    return ToolDecision.Reject(_options.GitRefusal);
                }

                return arguments.Any(a => a.StartsWith("--output", StringComparison.Ordinal))
                    ? ToolDecision.Reject("git --output writes a file; read the output directly.")
                    : arguments.Any(a => GitRunsPrograms.Any(f => a.StartsWith(f, StringComparison.Ordinal))
                        || (arguments[0] == "grep" && a.StartsWith('-') && !a.StartsWith("--", StringComparison.Ordinal) && a.Contains('O', StringComparison.Ordinal)))
                        ? ToolDecision.Reject("Options that run other programs (external diff, textconv, pager) are not allowed; use plain git diff/show/grep.")
                        : ToolDecision.Approve($"read-only git {arguments[0]}");

            case "find":
                return arguments.Any(FindWriteActions.Contains)
                    ? ToolDecision.Reject("find actions that run commands or delete files are not allowed.")
                    : ToolDecision.Approve("read-only find");

            case "sed":
                return arguments.Any(a => a.StartsWith("-i", StringComparison.Ordinal) || a.StartsWith("--in-place", StringComparison.Ordinal))
                    ? ToolDecision.Reject("Edit files with the edit tool, not sed -i.")
                    : IsPrintOnlySed(arguments)
                        ? ToolDecision.Approve("read-only sed")
                        : ToolDecision.Reject("Only printing sed scripts are allowed (such as sed -n '1,40p' file or s/a/b/g). Edit files with the edit tool.");

            // Read-only programs with options that write files or run other programs.
            case "sort" when arguments.Any(a => a.StartsWith("-o", StringComparison.Ordinal) || a.StartsWith("--output", StringComparison.Ordinal)):
            case "tree" when arguments.Any(a => a == "-o" || a.StartsWith("--output", StringComparison.Ordinal)):
            case "uniq" when arguments.Count(a => !a.StartsWith('-')) > 1:
            case "rg" when arguments.Any(a => a.StartsWith("--pre", StringComparison.Ordinal)):
                return ToolDecision.Reject($"That {command} option writes a file or runs a program. Read the output directly.");

            case "rm" or "del" or "remove-item" or "mv" or "move-item" or "cp" or "copy-item":
                return ToolDecision.Reject($"'{command}' is not allowed. Change code with the edit tool; leave files where they are.");

            default:
                return _options.ReadOnlyCommands.Contains(command) ? ToolDecision.Approve($"read-only {command}")
                    : _options.NetworkCommands.Contains(command) ? ToolDecision.Reject(_options.NetworkRefusal)
                    : ToolDecision.Reject(_options.UnknownCommandRefusal(command));
        }
    }

    private ToolDecision Sensitive(string path) => ToolDecision.Reject(_options.SensitiveRefusal(Path.GetFileName(path.TrimEnd('/', '\\'))));

    private static WorkspacePolicyOptions Configure(Action<WorkspacePolicyOptions> configure)
    {
        var options = new WorkspacePolicyOptions();
        configure(options);
        return options;
    }

    /// <summary>
    /// sed scripts that only print: line addresses with p, d or =, and s/// with only the g, i, p or number flags.
    /// Anything else (w, W, r, e, or an s flag of w or e) can write files or run programs.
    /// </summary>
    private static bool IsPrintOnlySed(IReadOnlyList<string> arguments)
    {
        var scripts = new List<string>();
        for (var i = 0; i < arguments.Count; i++)
        {
            var argument = arguments[i];
            if (argument is "-e" or "--expression" && i + 1 < arguments.Count)
            {
                scripts.Add(arguments[++i]);
            }
            else if (argument.StartsWith("--expression=", StringComparison.Ordinal))
            {
                scripts.Add(argument["--expression=".Length..]);
            }
            else if (argument is "-f" or "--file" || argument.StartsWith("--file=", StringComparison.Ordinal))
            {
                return false;
            }
            else if (!argument.StartsWith('-') && scripts.Count == 0)
            {
                scripts.Add(argument);
            }
        }

        return scripts.Count > 0 && scripts.SelectMany(s => s.Split(';', '\n')).All(c => c.Trim().Length == 0 || SedPrintCommand().IsMatch(c.Trim()));
    }

    [GeneratedRegex(@"^(\d+|\$|/[^/]*/)?(,(\d+|\$|/[^/]*/))?\s*([pd=]|s(.)((?!\5).)*\5((?!\5).)*\5[gIip0-9]*)$")]
    private static partial Regex SedPrintCommand();

    /// <summary>The path a word may name: the word itself, or the value of a <c>--option=value</c>.</summary>
    private static string? PathIn(string word) =>
        !word.StartsWith('-') ? word
        : word.IndexOf('=', StringComparison.Ordinal) is var i and > 0 && i < word.Length - 1 ? word[(i + 1)..]
        : null;

    /// <summary>The path in a git <c>revision:path</c> word; a Windows drive letter isn't a revision.</summary>
    private static string AfterRevision(string word)
    {
        var colon = word.LastIndexOf(':');
        return colon > 1 || (colon == 1 && !char.IsLetter(word[0])) ? word[(colon + 1)..] : word;
    }

    /// <summary>Relative paths without ".." stay inside the current folder, which is always inside the workspace.</summary>
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
