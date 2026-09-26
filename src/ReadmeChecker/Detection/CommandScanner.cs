using System.Text.RegularExpressions;

namespace ReadmeChecker.Detection;

internal sealed partial class CommandScanner(ReadmeFile readme, RepoFacts facts)
{
    private static readonly HashSet<string> FileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".fsproj", ".vbproj", ".sln", ".slnx", ".props", ".targets", ".json", ".md", ".ps1", ".psm1", ".sh",
        ".yml", ".yaml", ".xml", ".config", ".cmd", ".bat", ".py", ".ts", ".js", ".sql", ".tf", ".tfvars", ".http", ".txt",
    };

    private static readonly HashSet<string> BareFileNames = new(StringComparer.OrdinalIgnoreCase) { "Dockerfile", "Makefile" };

    private static readonly HashSet<string> ConventionalFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "src", "source", "tests", "test", "docs", "doc", "scripts", "samples", "tools", "build", "deploy", "infra", "charts", "helm",
        "terraform", "pipelines", "config",
    };

    private static readonly HashSet<string> GeneratedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "out", "artifacts", "node_modules", "TestResults", ".vs", "packages",
    };

    private readonly HashSet<string> _created = new(StringComparer.OrdinalIgnoreCase);
    private string _cwd = "";
    private bool _lost;

    public IEnumerable<Signal> Scan(string line, int lineNumber)
    {
        foreach (var tokens in ShellLine.Commands(line))
        {
            if (_lost)
            {
                yield break;
            }

            foreach (var signal in Run(CommandRules.Read(tokens), tokens, lineNumber))
            {
                yield return signal;
            }
        }
    }

    private IEnumerable<Signal> Run(CommandReading reading, IReadOnlyList<string> tokens, int line)
    {
        if (reading.LeavesRepository)
        {
            _lost = true;
            yield break;
        }

        _created.UnionWith(reading.Creates.Select(i => Clean(tokens[i])));

        if (reading.ChangesDirectoryTo is { } folder && ChangeDirectory(tokens[folder], line) is { } missingFolder)
        {
            yield return missingFolder;
        }

        if (_lost)
        {
            yield break;
        }

        foreach (var target in reading.Targets)
        {
            if (Missing(SignalKind.MissingCommandTarget, line, tokens[target.Index], target.Role) is { } signal)
            {
                yield return signal;
            }
        }

        if (!reading.ChecksOtherTokens)
        {
            yield break;
        }

        for (var i = 0; i < tokens.Count; i++)
        {
            if (!reading.Consumes(i) && Missing(SignalKind.MissingPath, line, tokens[i], PathRole.Mention) is { } signal)
            {
                yield return signal;
            }
        }
    }

    private Signal? ChangeDirectory(string token, int line)
    {
        var target = Clean(token);
        if (!LooksLikeRelativePath(target))
        {
            _lost = true;
            return null;
        }

        if (Resolve(target) is { } folder && facts.IsFolder(folder))
        {
            _cwd = folder;
            return null;
        }

        _lost = true;
        return IsCreated(target)
            ? null
            : new Signal(SignalKind.MissingCommandTarget, line, token, "cd into a folder that isn't in the repository", Resolve(target));
    }

    private Signal? Missing(SignalKind kind, int line, string token, PathRole role) =>
        MissingTarget(token, role) is { } missing ? Signal.NotInRepository(kind, line, token, missing) : null;

    private string? MissingTarget(string token, PathRole role)
    {
        var path = Clean(token);
        if (!LooksLikeRelativePath(path) || (role != PathRole.Project && !HasFileShape(path)) || IsCreated(path))
        {
            return null;
        }

        var first = path.Split('/')[0];
        if (GeneratedFolders.Contains(first) || path.Split('/').Any(s => s is "bin" or "obj"))
        {
            return null;
        }

        if (!path.Contains('/', StringComparison.Ordinal))
        {
            return role == PathRole.Mention ? null : MissingIfNotFound(path);
        }

        if (!HasFileExtension(path) && !facts.IsFolder(first) && !ConventionalFolders.Contains(first) && !(Resolve(first) is { } relativeFirst && facts.IsFolder(relativeFirst)))
        {
            return null;
        }

        return MissingIfNotFound(path);
    }

    private string? MissingIfNotFound(string path)
    {
        var candidates = new[] { RepoPaths.Normalize(RepoPaths.Join(_cwd, path)), RepoPaths.Normalize(RepoPaths.Join(readme.Directory, path)), RepoPaths.Normalize(path) };
        return candidates.Any(c => c is not null && facts.Exists(c)) ? null : candidates.FirstOrDefault(c => c is not null) ?? path;
    }

    private string? Resolve(string path) => RepoPaths.Normalize(RepoPaths.Join(_cwd, path));

    private bool IsCreated(string path) =>
        _created.Any(c => path.Equals(c, StringComparison.OrdinalIgnoreCase) || path.StartsWith(c + "/", StringComparison.OrdinalIgnoreCase));

    private static bool HasFileExtension(string path) => FileExtensions.Contains(Path.GetExtension(path));

    private static bool LooksLikeRelativePath(string path) =>
        path.Length > 0 && !path.StartsWith('-') && !path.StartsWith('~') && !path.StartsWith('/') && path is not ("." or "..")
        && Path.GetFileNameWithoutExtension(path.TrimEnd('/')).Length > 0
        && !path.Contains("://", StringComparison.Ordinal) && !NotAPath().IsMatch(path);

    private static bool HasFileShape(string path) =>
        path.Contains('/', StringComparison.Ordinal) || HasFileExtension(path) || BareFileNames.Contains(path);

    private static string Clean(string token)
    {
        var cleaned = token.Trim('"', '\'', '`', '(', ')', ',', ';').Replace('\\', '/');
        while (cleaned.StartsWith("./", StringComparison.Ordinal))
        {
            cleaned = cleaned[2..];
        }

        return cleaned.TrimEnd('.', ':');
    }

    [GeneratedRegex(@"[<>{}*?\[\]|=$%@:]|^[A-Za-z]:")]
    private static partial Regex NotAPath();
}
