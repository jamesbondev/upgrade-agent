using System.Globalization;
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

    public static Signal NotInRepository(SignalKind kind, int line, string text, string missing) =>
        new(kind, line, text, $"'{missing}' is not in the repository", missing);
}

internal sealed record SignalScan(IReadOnlyList<Signal> Signals, int Dropped)
{
    public static SignalScan Empty { get; } = new([], 0);

    public static SignalScan Of(IEnumerable<Signal> signals) =>
        new(signals.DistinctBy(s => (s.Kind, s.Text)).OrderBy(s => s.Kind).ThenBy(s => s.Line).ToList(), 0);

    public SignalScan Capped(int max) =>
        Signals.Count <= max ? this : new SignalScan(Signals.Take(max).ToList(), Dropped + Signals.Count - max);
}

internal static partial class ReadmeSignals
{
    private const int MinListedFiles = 3;
    private const int NearbyLinkLines = 15;
    private const int OrLaterLookahead = 24;
    private const int MinProjectsInGroup = 3;

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UsePipeTables().Build();

    private static readonly HashSet<string> ShellLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "sh", "bash", "shell", "console", "zsh", "pwsh", "powershell", "ps", "ps1", "cmd", "bat", "batch",
    };

    private static readonly HashSet<string> ProseLikeLanguages = new(StringComparer.OrdinalIgnoreCase) { "", "text", "txt", "plaintext" };

    public static SignalScan Find(RepoFacts facts)
    {
        if (facts.Readme is not { IsSymlink: false } readme)
        {
            return SignalScan.Empty;
        }

        var document = Markdown.Parse(readme.Text, Pipeline);
        return SignalScan.Of(Links(document, readme, facts)
            .Concat(Code(document, readme, facts))
            .Concat(Versions(readme.Text, facts))
            .Concat(UnlistedFiles(document, readme, facts))
            .Concat(UnmentionedProjects(readme, facts)));
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
            if (ResolveLink(url, readme.Directory) is { } missing && !facts.Exists(missing))
            {
                yield return Signal.NotInRepository(SignalKind.BrokenLink, line + 1, url, missing);
            }
        }
    }

    private static IEnumerable<(string Url, int Line)> HtmlTargets(string html, int line) =>
        HtmlTarget().Matches(html).Select(m => (m.Groups["url"].Value, line));

    private static string? ResolveLink(string url, string readmeDirectory)
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

        return target.StartsWith('/') ? RepoPaths.Normalize(target.TrimStart('/')) : RepoPaths.Normalize(RepoPaths.Join(readmeDirectory, target));
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

        foreach (var group in linked.GroupBy(l => RepoPaths.FolderOf(l.Path)))
        {
            var listed = group.Select(l => l.Path).ToHashSet(StringComparer.Ordinal);
            if (listed.Count < MinListedFiles)
            {
                continue;
            }

            var extension = listed.GroupBy(Path.GetExtension).OrderByDescending(g => g.Count()).First().Key;
            var siblings = facts.Files
                .Where(f => RepoPaths.FolderOf(f) == group.Key && Path.GetExtension(f) == extension && !IsIndexLike(f))
                .ToList();
            var unlisted = siblings
                .Where(f => !listed.Contains(f) && !readme.Text.Contains(Path.GetFileName(f), StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (siblings.Count == 0 || siblings.Count - unlisted.Count < siblings.Count / 2.0)
            {
                continue;
            }

            var line = group.Select(l => l.Line).OrderByDescending(l => group.Count(o => Math.Abs(o.Line - l) <= NearbyLinkLines)).First();
            var folder = group.Key.Length == 0 ? "the repository root" : $"{group.Key}/";
            foreach (var file in unlisted)
            {
                yield return new Signal(
                    SignalKind.UnlistedFile, line, file, $"the README lists {siblings.Count - unlisted.Count} of the {siblings.Count} files in {folder}, but not this one", file);
            }
        }
    }

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
                if (OrLater().IsMatch(text.AsSpan(match.Index + match.Length, Math.Min(OrLaterLookahead, text.Length - match.Index - match.Length))))
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
            var enumerated = group.Count() >= MinProjectsInGroup && mentioned.Count >= group.Count() / 2.0;
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
}
