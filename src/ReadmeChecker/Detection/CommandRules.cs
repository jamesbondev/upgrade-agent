namespace ReadmeChecker.Detection;

internal enum PathRole
{
    Project,
    Script,
    Mention,
}

internal sealed record CommandTarget(int Index, PathRole Role);

internal sealed record CommandReading
{
    public static CommandReading Unknown { get; } = new();

    public IReadOnlyList<int> CommandWords { get; init; } = [];

    public IReadOnlyList<CommandTarget> Targets { get; init; } = [];

    public int? ChangesDirectory { get; init; }

    public IReadOnlyList<string> Creates { get; init; } = [];

    public bool LeavesRepository { get; init; }

    public bool ChecksOtherTokens { get; init; } = true;

    public bool Accounts(int index) =>
        CommandWords.Contains(index) || index == ChangesDirectory || Targets.Any(t => t.Index == index);
}

internal static class CommandRules
{
    private static readonly HashSet<string> DotnetProjectVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "run", "test", "build", "pack", "publish", "restore", "clean", "watch",
    };

    private static readonly Func<IReadOnlyList<string>, CommandReading?>[] Rules =
    [
        ChangeDirectory,
        MakeDirectory,
        GitClone,
        DotnetNew,
        Dotnet,
        RunScript,
    ];

    public static CommandReading Read(IReadOnlyList<string> tokens) =>
        Rules.Select(rule => rule(tokens)).FirstOrDefault(reading => reading is not null) ?? CommandReading.Unknown;

    private static CommandReading? ChangeDirectory(IReadOnlyList<string> tokens) =>
        tokens is ["cd" or "pushd" or "Set-Location" or "sl", _, ..]
            ? new CommandReading { CommandWords = [0], ChangesDirectory = 1 }
            : null;

    private static CommandReading? MakeDirectory(IReadOnlyList<string> tokens) =>
        tokens is ["mkdir" or "md" or "New-Item", ..]
            ? new CommandReading { Creates = tokens.Skip(1).Where(t => !t.StartsWith('-')).ToList(), ChecksOtherTokens = false }
            : null;

    private static CommandReading? GitClone(IReadOnlyList<string> tokens) =>
        tokens is ["git", "clone", _, ..] ? new CommandReading { LeavesRepository = true } : null;

    private static CommandReading? DotnetNew(IReadOnlyList<string> tokens) =>
        tokens is ["dotnet", "new", ..]
            ? new CommandReading { Creates = OptionValues(tokens, "-o", "--output", "-n", "--name"), ChecksOtherTokens = false }
            : null;

    private static CommandReading? Dotnet(IReadOnlyList<string> tokens) =>
        tokens is ["dotnet", var verb, ..]
            ? DotnetProjectVerbs.Contains(verb) ? ProjectCommand(tokens) : new CommandReading { CommandWords = [0, 1] }
            : null;

    private static CommandReading? RunScript(IReadOnlyList<string> tokens) =>
        ScriptIndex(tokens) is { } script
            ? new CommandReading { CommandWords = [0], Targets = [new CommandTarget(script, PathRole.Script)] }
            : null;

    private static CommandReading ProjectCommand(IReadOnlyList<string> tokens)
    {
        var words = new List<int> { 0, 1 };
        var targets = new List<CommandTarget>();
        for (var i = 2; i < tokens.Count && tokens[i] != "--"; i++)
        {
            if (tokens[i] is "--project" or "-p" && i + 1 < tokens.Count)
            {
                words.Add(i);
                targets.Add(new CommandTarget(i + 1, PathRole.Project));
            }
            else if (i == 2 && !tokens[i].StartsWith('-'))
            {
                targets.Add(new CommandTarget(i, PathRole.Project));
            }
        }

        return new CommandReading { CommandWords = words, Targets = targets };
    }

    private static List<string> OptionValues(IReadOnlyList<string> tokens, params string[] options)
    {
        var values = new List<string>();
        for (var i = 2; i < tokens.Count - 1; i++)
        {
            if (options.Contains(tokens[i]))
            {
                values.Add(tokens[i + 1]);
            }
        }

        return values;
    }

    private static int? ScriptIndex(IReadOnlyList<string> tokens)
    {
        if (IsScript(tokens[0]))
        {
            return 0;
        }

        if (tokens[0] is not ("pwsh" or "powershell" or "bash" or "sh"))
        {
            return null;
        }

        for (var i = 1; i < tokens.Count; i++)
        {
            if (IsScript(tokens[i]) || tokens[i].EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) || tokens[i].EndsWith(".sh", StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return null;
    }

    private static bool IsScript(string token) =>
        token.StartsWith("./", StringComparison.Ordinal) || token.StartsWith(".\\", StringComparison.Ordinal);
}
