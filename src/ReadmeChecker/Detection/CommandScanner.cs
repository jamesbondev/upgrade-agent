using System.Text;
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

    private static readonly HashSet<string> DotnetProjectVerbs = new(StringComparer.OrdinalIgnoreCase)
    {
        "run", "test", "build", "pack", "publish", "restore", "clean", "watch",
    };

    private readonly HashSet<string> _created = new(StringComparer.OrdinalIgnoreCase);
    private string _cwd = "";
    private bool _lost;

    public IEnumerable<Signal> Scan(string line, int lineNumber)
    {
        if (_lost)
        {
            yield break;
        }

        var text = Prompt().Replace(line, "").Trim();
        var comment = text.IndexOf(" #", StringComparison.Ordinal);
        if (comment >= 0)
        {
            text = text[..comment].TrimEnd();
        }

        if (text.Length == 0 || text.StartsWith('#') || text.StartsWith("//", StringComparison.Ordinal) || text.StartsWith("REM ", StringComparison.OrdinalIgnoreCase))
        {
            yield break;
        }

        foreach (var segment in Segments(Tokenize(text)))
        {
            foreach (var signal in ScanSegment(segment, lineNumber))
            {
                yield return signal;
            }

            if (_lost)
            {
                yield break;
            }
        }
    }

    private IEnumerable<Signal> ScanSegment(IReadOnlyList<string> tokens, int line)
    {
        var command = tokens[0];
        var consumed = new HashSet<int>();

        if (command is "cd" or "pushd" or "Set-Location" or "sl" && tokens.Count > 1)
        {
            consumed.UnionWith([0, 1]);
            var target = Clean(tokens[1]);
            if (!LooksLikeRelativePath(target, requireFileShape: false))
            {
                _lost = true;
                yield break;
            }

            if (Resolve(target) is { } folder && facts.IsFolder(folder))
            {
                _cwd = folder;
            }
            else
            {
                if (!IsCreated(target))
                {
                    yield return new Signal(SignalKind.MissingCommandTarget, line, tokens[1], "cd into a folder that isn't in the repository", Resolve(target));
                }

                _lost = true;
                yield break;
            }
        }
        else if (command is "mkdir" or "md" or "New-Item")
        {
            _created.UnionWith(tokens.Skip(1).Where(t => !t.StartsWith('-')).Select(Clean));
            yield break;
        }
        else if (command == "git" && tokens.Count > 2 && tokens[1] == "clone")
        {
            _lost = true;
            yield break;
        }
        else if (command == "dotnet" && tokens.Count > 1)
        {
            consumed.UnionWith([0, 1]);
            if (tokens[1] == "new")
            {
                for (var i = 2; i < tokens.Count - 1; i++)
                {
                    if (tokens[i] is "-o" or "--output" or "-n" or "--name")
                    {
                        _created.Add(Clean(tokens[i + 1]));
                    }
                }

                yield break;
            }

            if (DotnetProjectVerbs.Contains(tokens[1]))
            {
                for (var i = 2; i < tokens.Count; i++)
                {
                    if (tokens[i] == "--")
                    {
                        break;
                    }

                    var isProjectOption = tokens[i] is "--project" or "-p" && i + 1 < tokens.Count;
                    var index = isProjectOption ? i + 1 : i;
                    if (!isProjectOption && (tokens[i].StartsWith('-') || i > 2))
                    {
                        continue;
                    }

                    consumed.Add(i);
                    consumed.Add(index);
                    if (Missing(SignalKind.MissingCommandTarget, line, tokens[index], requireFileShape: false, exact: true) is { } signal)
                    {
                        yield return signal;
                    }
                }
            }
        }
        else if (ScriptIndex(tokens) is { } script)
        {
            consumed.UnionWith([0, script]);
            if (Missing(SignalKind.MissingCommandTarget, line, tokens[script], requireFileShape: true, exact: true) is { } signal)
            {
                yield return signal;
            }
        }

        for (var i = 0; i < tokens.Count; i++)
        {
            if (consumed.Contains(i))
            {
                continue;
            }

            if (Missing(SignalKind.MissingPath, line, tokens[i], requireFileShape: true) is { } signal)
            {
                yield return signal;
            }
        }
    }

    private Signal? Missing(SignalKind kind, int line, string token, bool requireFileShape, bool exact = false) =>
        MissingTarget(token, requireFileShape, exact) is { } missing ? Signal.NotInRepository(kind, line, token, missing) : null;

    private string? MissingTarget(string token, bool requireFileShape, bool exact)
    {
        var path = Clean(token);
        if (!LooksLikeRelativePath(path, requireFileShape) || IsCreated(path))
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
            return exact ? MissingIfNotFound(path) : null;
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

    private static bool HasFileExtension(string path) => FileExtensions.Contains(Path.GetExtension(path));

    private static bool LooksLikeRelativePath(string path, bool requireFileShape)
    {
        if (path.Length == 0 || path.StartsWith('-') || path.StartsWith('~') || path.StartsWith('/') || path is "." or ".."
            || Path.GetFileNameWithoutExtension(path.TrimEnd('/')).Length == 0
            || path.Contains("://", StringComparison.Ordinal) || NotAPath().IsMatch(path))
        {
            return false;
        }

        return !requireFileShape || path.Contains('/', StringComparison.Ordinal) || HasFileExtension(path) || BareFileNames.Contains(path);
    }

    private static string Clean(string token)
    {
        var cleaned = token.Trim('"', '\'', '`', '(', ')', ',', ';').Replace('\\', '/');
        while (cleaned.StartsWith("./", StringComparison.Ordinal))
        {
            cleaned = cleaned[2..];
        }

        return cleaned.TrimEnd('.', ':');
    }

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        foreach (var c in text)
        {
            if (quote is not null)
            {
                if (c == quote)
                {
                    quote = null;
                }
                else
                {
                    current.Append(c);
                }
            }
            else if (c is '"' or '\'')
            {
                quote = c;
            }
            else if (char.IsWhiteSpace(c))
            {
                Flush();
            }
            else
            {
                current.Append(c);
            }
        }

        Flush();
        return tokens;

        void Flush()
        {
            if (current.Length > 0)
            {
                tokens.Add(current.ToString());
                current.Clear();
            }
        }
    }

    private static IEnumerable<IReadOnlyList<string>> Segments(List<string> tokens)
    {
        var segment = new List<string>();
        foreach (var token in tokens)
        {
            if (token is "&&" or "||" or ";" or "|")
            {
                if (segment.Count > 0)
                {
                    yield return segment;
                }

                segment = [];
            }
            else
            {
                segment.Add(token);
            }
        }

        if (segment.Count > 0)
        {
            yield return segment;
        }
    }

    [GeneratedRegex(@"^\s*(?:\$|>|PS[^>]*>)\s+")]
    private static partial Regex Prompt();

    [GeneratedRegex(@"[<>{}*?\[\]|=$%@:]|^[A-Za-z]:")]
    private static partial Regex NotAPath();
}
