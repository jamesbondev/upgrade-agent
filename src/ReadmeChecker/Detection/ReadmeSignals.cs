using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace ReadmeChecker.Detection;

internal enum SignalKind
{
    BrokenLink,
    MissingCommandTarget,
    MissingPath,
    MissingIdentifier,
    VersionMismatch,
    UnlistedFile,
    UnmentionedProject,
}

internal sealed record Signal(SignalKind Kind, int Line, string Text, string Detail, string? Target = null)
{
    public bool Definitive => Kind == SignalKind.BrokenLink;
}

internal sealed record SignalScan(IReadOnlyList<Signal> Signals, int Dropped)
{
    public static SignalScan Empty { get; } = new([], 0);

    public SignalScan Capped(int max) =>
        Signals.Count <= max ? this : new SignalScan(Signals.Take(max).ToList(), Dropped + Signals.Count - max);
}

internal static partial class ReadmeSignals
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().Build();

    private static readonly HashSet<string> ShellLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "sh", "bash", "shell", "console", "zsh", "pwsh", "powershell", "ps", "ps1", "cmd", "bat", "batch",
    };

    private static readonly HashSet<string> ProseLikeLanguages = new(StringComparer.OrdinalIgnoreCase) { "", "text", "txt", "plaintext" };

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

    public static SignalScan Find(RepoFacts facts)
    {
        if (facts.Readme is not { IsSymlink: false } readme)
        {
            return SignalScan.Empty;
        }

        var document = Markdown.Parse(readme.Text, Pipeline);
        var all = Links(document, readme, facts)
            .Concat(Code(document, readme, facts))
            .Concat(Versions(readme.Text, facts))
            .Concat(UnlistedFiles(document, readme, facts))
            .Concat(UnmentionedProjects(readme, facts))
            .DistinctBy(s => (s.Kind, s.Text))
            .OrderBy(s => s.Kind)
            .ThenBy(s => s.Line)
            .ToList();

        return new SignalScan(all, 0);
    }

    private static IEnumerable<Signal> Links(MarkdownDocument document, ReadmeFile readme, RepoFacts facts)
    {
        var targets = document.Descendants<LinkInline>()
            .Where(l => l.Url is not null)
            .Select(l => (Url: l.Url!, l.Line))
            .Concat(document.Descendants<HtmlInline>().SelectMany(h => HtmlTargets(h.Tag, h.Line)))
            .Concat(document.Descendants<HtmlBlock>().SelectMany(h => HtmlTargets(h.Lines.ToString(), h.Line)));

        foreach (var (url, line) in targets)
        {
            if (MissingLinkTarget(url, readme.Directory, facts) is { } missing)
            {
                yield return new Signal(SignalKind.BrokenLink, line + 1, url, $"'{missing}' is not in the repository", missing);
            }
        }
    }

    private static IEnumerable<(string Url, int Line)> HtmlTargets(string html, int line) =>
        HtmlTarget().Matches(html).Select(m => (m.Groups["url"].Value, line));

    internal static string? MissingLinkTarget(string url, string readmeDirectory, RepoFacts facts) =>
        ResolveLink(url, readmeDirectory) is { } resolved && !facts.Exists(resolved) ? resolved : null;

    internal static string? ResolveLink(string url, string readmeDirectory)
    {
        var target = url.Trim();
        if (target.Length == 0 || target.StartsWith('#') || target.StartsWith("//", StringComparison.Ordinal) || UrlScheme().IsMatch(target))
        {
            return null;
        }

        var end = target.IndexOfAny(['#', '?']);
        if (end >= 0)
        {
            target = target[..end];
        }

        try
        {
            target = Uri.UnescapeDataString(target);
        }
        catch (UriFormatException)
        {
            return null;
        }

        return target.StartsWith('/') ? Normalize(target.TrimStart('/')) : Normalize(Join(readmeDirectory, target));
    }

    public static IReadOnlyList<(string Identifier, int Line)> IdentifierMentions(string text)
    {
        var examples = Markdown.Parse(text, Pipeline).Descendants<FencedCodeBlock>()
            .Where(b => !ProseLikeLanguages.Contains(LanguageOf(b)))
            .Select(b => (First: b.Line + 1, Last: b.Line + 1 + b.Lines.Count + 1))
            .ToList();

        return Identifier().Matches(text)
            .Select(m => (m.Value, Line: LineOf(text, m.Index)))
            .Where(m => !examples.Any(e => m.Line >= e.First && m.Line <= e.Last))
            .DistinctBy(m => m.Value)
            .ToList();
    }

    private static IEnumerable<Signal> UnlistedFiles(MarkdownDocument document, ReadmeFile readme, RepoFacts facts)
    {
        var linked = document.Descendants<LinkInline>()
            .Where(l => l.Url is not null)
            .Select(l => (Path: ResolveLink(l.Url!, readme.Directory), Line: l.Line + 1))
            .Where(l => l.Path is not null && facts.IsFile(l.Path))
            .Select(l => (Path: l.Path!, l.Line))
            .ToList();

        foreach (var group in linked.GroupBy(l => FolderOf(l.Path)))
        {
            var listed = group.Select(l => l.Path).ToHashSet(StringComparer.Ordinal);
            if (listed.Count < 3)
            {
                continue;
            }

            var extension = listed.GroupBy(Path.GetExtension).OrderByDescending(g => g.Count()).First().Key;
            var siblings = facts.Files
                .Where(f => FolderOf(f) == group.Key && Path.GetExtension(f) == extension && !IsIndexLike(f))
                .ToList();
            var unlisted = siblings
                .Where(f => !listed.Contains(f) && !readme.Text.Contains(Path.GetFileName(f), StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (siblings.Count == 0 || siblings.Count - unlisted.Count < siblings.Count / 2.0)
            {
                continue;
            }

            var line = group.Select(l => l.Line).OrderByDescending(l => group.Count(o => Math.Abs(o.Line - l) <= 15)).First();
            var folder = group.Key.Length == 0 ? "the repository root" : $"{group.Key}/";
            foreach (var file in unlisted)
            {
                yield return new Signal(
                    SignalKind.UnlistedFile, line, file, $"the README lists {siblings.Count - unlisted.Count} of the {siblings.Count} files in {folder}, but not this one", file);
            }
        }
    }

    private static string FolderOf(string path) => path.Contains('/', StringComparison.Ordinal) ? path[..path.LastIndexOf('/')] : "";

    private static bool IsIndexLike(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.Equals("index", StringComparison.OrdinalIgnoreCase)
            || name.Equals("README", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("00-", StringComparison.Ordinal)
            || name.StartsWith('_')
            || name.Contains("template", StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<Signal> Code(MarkdownDocument document, ReadmeFile readme, RepoFacts facts)
    {
        foreach (var inline in document.Descendants<CodeInline>())
        {
            var scanner = new CommandScanner(readme, facts);
            foreach (var signal in scanner.Scan(inline.Content, inline.Line + 1))
            {
                yield return signal;
            }
        }

        foreach (var block in document.Descendants<CodeBlock>())
        {
            if (!ShellLanguages.Contains(LanguageOf(block)))
            {
                continue;
            }

            var scanner = new CommandScanner(readme, facts);
            foreach (var line in block.Lines.Lines.Take(block.Lines.Count))
            {
                foreach (var signal in scanner.Scan(line.Slice.ToString(), line.Line + 1))
                {
                    yield return signal;
                }
            }
        }
    }

    private static string LanguageOf(CodeBlock block) => block is FencedCodeBlock fenced ? fenced.Info ?? "" : "";

    private static IEnumerable<Signal> Versions(string text, RepoFacts facts)
    {
        if (facts.TargetFrameworks.Count > 0)
        {
            var targets = $"the projects target {string.Join(", ", facts.TargetFrameworks.Order(StringComparer.OrdinalIgnoreCase))}";
            foreach (Match match in TargetFrameworkMention().Matches(text))
            {
                var mentioned = match.Groups["tfm"].Value;
                if (!facts.TargetFrameworks.Any(t => t.StartsWith(mentioned, StringComparison.OrdinalIgnoreCase)))
                {
                    yield return Mismatch(match, targets);
                }
            }

            foreach (Match match in DotnetMention().Matches(text))
            {
                if (OrLater().IsMatch(text.AsSpan(match.Index + match.Length, Math.Min(24, text.Length - match.Index - match.Length))))
                {
                    continue;
                }

                var major = int.Parse(match.Groups["major"].Value, CultureInfo.InvariantCulture);
                var prefix = major >= 5 ? $"net{major}." : $"netcoreapp{major}.";
                if (!facts.TargetFrameworks.Any(t => t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                {
                    yield return Mismatch(match, targets);
                }
            }
        }

        if (facts.SdkVersion is { } sdk)
        {
            var sdkMajor = sdk.Split('.')[0];
            foreach (Match match in SdkMention().Matches(text))
            {
                if (match.Groups["major"].Value != sdkMajor)
                {
                    yield return Mismatch(match, $"global.json pins SDK {sdk}");
                }
            }
        }

        Signal Mismatch(Match match, string detail) => new(SignalKind.VersionMismatch, LineOf(text, match.Index), match.Value, detail);
    }

    private static IEnumerable<Signal> UnmentionedProjects(ReadmeFile readme, RepoFacts facts)
    {
        var scope = readme.Directory is { Length: > 0 } directory ? directory + "/" : "";
        var projects = facts.Projects.Where(f => IsProjectFile(f) && f.StartsWith(scope, StringComparison.Ordinal)).ToList();
        var added = (facts.Age?.AddedProjects ?? []).ToHashSet(StringComparer.Ordinal);

        foreach (var group in projects.GroupBy(p => p[scope.Length..].Split('/')[0]))
        {
            var mentioned = group.Where(p => Mentions(readme.Text, p)).ToList();
            var enumerated = group.Count() >= 3 && mentioned.Count >= group.Count() / 2.0;
            var listsTests = mentioned.Any(IsTestProject);
            foreach (var project in group.Except(mentioned))
            {
                var isTest = IsTestProject(project);
                if (enumerated && (!isTest || listsTests))
                {
                    yield return new Signal(
                        SignalKind.UnmentionedProject, 0, project, $"the README names {mentioned.Count} of the {group.Count()} projects under {scope}{group.Key}/, but not this one", project);
                }
                else if (added.Contains(project) && !isTest)
                {
                    yield return new Signal(SignalKind.UnmentionedProject, 0, project, "added since the README last changed, and not mentioned in it", project);
                }
            }
        }
    }

    private static bool IsProjectFile(string path) =>
        path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase);

    private static bool Mentions(string text, string project)
    {
        var folder = Path.GetDirectoryName(project)?.Replace('\\', '/') ?? "";
        return text.Contains(Path.GetFileNameWithoutExtension(project), StringComparison.OrdinalIgnoreCase)
            || (folder.Length > 0 && text.Contains(folder, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsTestProject(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.EndsWith("Tests", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".Test", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("Benchmarks", StringComparison.OrdinalIgnoreCase)
            || path.Split('/').Any(s => s.Equals("tests", StringComparison.OrdinalIgnoreCase) || s.Equals("test", StringComparison.OrdinalIgnoreCase));
    }

    private static int LineOf(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    internal static string? Normalize(string path)
    {
        var parts = new List<string>();
        foreach (var segment in path.Replace('\\', '/').Split('/'))
        {
            switch (segment)
            {
                case "" or ".":
                    continue;
                case "..":
                    if (parts.Count == 0)
                    {
                        return null;
                    }

                    parts.RemoveAt(parts.Count - 1);
                    break;
                default:
                    parts.Add(segment);
                    break;
            }
        }

        return string.Join('/', parts);
    }

    private static string Join(string folder, string path) => folder.Length == 0 ? path : $"{folder}/{path}";

    [GeneratedRegex("""(?:src|href)\s*=\s*["'](?<url>[^"']+)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlTarget();

    [GeneratedRegex(@"\b(?:I[A-Z][a-z0-9]+(?:[A-Z][a-z0-9]*)+|[A-Z][a-z0-9]+(?:[A-Z][a-z0-9]+){2,})\b")]
    private static partial Regex Identifier();

    [GeneratedRegex("^[a-zA-Z][a-zA-Z0-9+.-]*:")]
    private static partial Regex UrlScheme();

    [GeneratedRegex(@"\b(?<tfm>net\d{1,2}\.\d|netcoreapp\d\.\d)(?![\d.])", RegexOptions.IgnoreCase)]
    private static partial Regex TargetFrameworkMention();

    [GeneratedRegex(@"\.NET\s+(?:Core\s+)?(?<major>\d{1,2})(?:\.\d+)?(?![\d.]*\d)")]
    private static partial Regex DotnetMention();

    [GeneratedRegex(@"^\s*(?:\+|or\s+(?:later|higher|newer|above|greater)|and\s+(?:later|above|up|newer))", RegexOptions.IgnoreCase)]
    private static partial Regex OrLater();

    [GeneratedRegex(@"\bSDK\b\D{0,20}?(?<major>\d{1,2})\.\d+\.\d{3}", RegexOptions.IgnoreCase)]
    private static partial Regex SdkMention();

    private sealed partial class CommandScanner(ReadmeFile readme, RepoFacts facts)
    {
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
                        if (MissingTarget(tokens[index], requireFileShape: false, exact: true) is { } missing)
                        {
                            yield return new Signal(SignalKind.MissingCommandTarget, line, tokens[index], $"'{missing}' is not in the repository", missing);
                        }
                    }
                }
            }
            else if (IsScript(command))
            {
                consumed.Add(0);
                if (MissingTarget(command, requireFileShape: true, exact: true) is { } missing)
                {
                    yield return new Signal(SignalKind.MissingCommandTarget, line, command, $"'{missing}' is not in the repository", missing);
                }
            }
            else if (command is "pwsh" or "powershell" or "bash" or "sh" && tokens.Skip(1).FirstOrDefault(t => IsScript(t) || t.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) || t.EndsWith(".sh", StringComparison.OrdinalIgnoreCase)) is { } script)
            {
                consumed.UnionWith([0, tokens.ToList().IndexOf(script)]);
                if (MissingTarget(script, requireFileShape: true, exact: true) is { } missing)
                {
                    yield return new Signal(SignalKind.MissingCommandTarget, line, script, $"'{missing}' is not in the repository", missing);
                }
            }

            for (var i = 0; i < tokens.Count; i++)
            {
                if (consumed.Contains(i))
                {
                    continue;
                }

                if (MissingTarget(tokens[i], requireFileShape: true) is { } missing)
                {
                    yield return new Signal(SignalKind.MissingPath, line, tokens[i], $"'{missing}' is not in the repository", missing);
                }
            }
        }

        private string? MissingTarget(string token, bool requireFileShape, bool exact = false)
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
            var candidates = new[] { Normalize(Join(_cwd, path)), Normalize(Join(readme.Directory, path)), Normalize(path) };
            return candidates.Any(c => c is not null && facts.Exists(c)) ? null : candidates.FirstOrDefault(c => c is not null) ?? path;
        }

        private string? Resolve(string path) => Normalize(Join(_cwd, path));

        private bool IsCreated(string path) =>
            _created.Any(c => path.Equals(c, StringComparison.OrdinalIgnoreCase) || path.StartsWith(c + "/", StringComparison.OrdinalIgnoreCase));

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
}
