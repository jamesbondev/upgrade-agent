using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using RepoKit;

namespace ReadmeChecker.Detection;

internal sealed record ReadmeFile(string Path, string Text, bool IsSymlink)
{
    public string Directory => System.IO.Path.GetDirectoryName(Path)?.Replace('\\', '/') ?? "";
}

internal sealed record FolderChurn(string Folder, int Files);

internal sealed record ReadmeAge(GitCommit LastChange, int CommitsSince, IReadOnlyList<string> AddedProjects, IReadOnlyList<FolderChurn> BusiestFolders);

internal sealed record RepoFacts(
    IReadOnlyList<string> Files,
    ReadmeFile? Readme,
    IReadOnlyList<string> Projects,
    IReadOnlySet<string> TargetFrameworks,
    string? SdkVersion,
    ReadmeAge? Age)
{
    private static readonly string[] ProjectExtensions = [".csproj", ".fsproj", ".vbproj", ".sln", ".slnx"];
    private static readonly string[] RootReadmeNames = ["README.md", "README.markdown", "README"];
    private static readonly XmlReaderSettings SafeXml = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };

    private readonly HashSet<string> _files = new(Files, StringComparer.Ordinal);
    private readonly HashSet<string> _folders = new(Files.SelectMany(ParentFolders), StringComparer.Ordinal);
    private readonly ILookup<string, string> _byFileName = Files.ToLookup(f => System.IO.Path.GetFileName(f), StringComparer.Ordinal);

    public bool IsFile(string path) => _files.Contains(path);

    public bool IsFolder(string path) => path.Length == 0 || _folders.Contains(path.TrimEnd('/'));

    public bool Exists(string path) => IsFile(path) || IsFolder(path);

    public bool FileNameExistsAnywhere(string fileName) => _byFileName.Contains(fileName);

    public static async Task<RepoFacts> CollectAsync(string root, GitCli git, string? configuredReadme, CancellationToken cancellationToken)
    {
        var files = await git.ListFilesAsync(root, cancellationToken);
        var readme = await FindReadmeAsync(root, git, files, configuredReadme, cancellationToken);
        var projects = files.Where(f => ProjectExtensions.Any(e => f.EndsWith(e, StringComparison.OrdinalIgnoreCase))).ToList();
        var age = readme is { IsSymlink: false } ? await AgeAsync(root, git, readme.Path, cancellationToken) : null;
        return new RepoFacts(files, readme, projects, TargetFrameworksIn(root, files), SdkVersionIn(root, files), age);
    }

    internal static IReadOnlySet<string> TargetFrameworksIn(string root, IReadOnlyList<string> files)
    {
        var frameworks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sources = files.Where(f =>
            f.EndsWith("proj", StringComparison.OrdinalIgnoreCase)
            || System.IO.Path.GetFileName(f) is var name
                && (name.Equals("Directory.Build.props", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Directory.Build.targets", StringComparison.OrdinalIgnoreCase)));

        foreach (var file in sources)
        {
            try
            {
                using var reader = XmlReader.Create(System.IO.Path.Combine(root, file), SafeXml);
                var document = XDocument.Load(reader);
                var values = document.Descendants()
                    .Where(e => e.Name.LocalName is "TargetFramework" or "TargetFrameworks")
                    .SelectMany(e => e.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    .Where(v => !v.Contains("$(", StringComparison.Ordinal));
                frameworks.UnionWith(values);
            }
            catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
            {
            }
        }

        return frameworks;
    }

    internal static string? SdkVersionIn(string root, IReadOnlyList<string> files)
    {
        if (!files.Contains("global.json"))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(root, "global.json")), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return document.RootElement.TryGetProperty("sdk", out var sdk) && sdk.TryGetProperty("version", out var version) ? version.GetString() : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    private static async Task<ReadmeFile?> FindReadmeAsync(
        string root, GitCli git, IReadOnlyList<string> files, string? configuredReadme, CancellationToken cancellationToken)
    {
        var path = configuredReadme is not null
            ? files.FirstOrDefault(f => f.Equals(configuredReadme, StringComparison.OrdinalIgnoreCase))
            : RootReadmeNames.Select(n => files.FirstOrDefault(f => f.Equals(n, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(f => f is not null);
        if (path is null)
        {
            return null;
        }

        var isSymlink = await git.FileModeAsync(root, path, cancellationToken) == "120000";
        var text = await File.ReadAllTextAsync(System.IO.Path.Combine(root, path), cancellationToken);
        return new ReadmeFile(path, text, isSymlink);
    }

    private static async Task<ReadmeAge?> AgeAsync(string root, GitCli git, string readmePath, CancellationToken cancellationToken)
    {
        if (await git.LastCommitTouchingAsync(root, readmePath, cancellationToken) is not { } lastChange)
        {
            return null;
        }

        var commitsSince = await git.CommitCountSinceAsync(root, lastChange.Sha, [readmePath], cancellationToken);
        var added = await git.PathsChangedSinceAsync(root, lastChange.Sha, "A", cancellationToken);
        var changed = await git.PathsChangedSinceAsync(root, lastChange.Sha, cancellationToken: cancellationToken);
        var busiest = changed
            .Where(p => p != readmePath)
            .GroupBy(p => p.Contains('/', StringComparison.Ordinal) ? p[..p.IndexOf('/', StringComparison.Ordinal)] : "(root)")
            .Select(g => new FolderChurn(g.Key, g.Count()))
            .OrderByDescending(f => f.Files)
            .ThenBy(f => f.Folder, StringComparer.Ordinal)
            .Take(5)
            .ToList();
        var addedProjects = added.Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)).ToList();
        return new ReadmeAge(lastChange, commitsSince, addedProjects, busiest);
    }

    private static IEnumerable<string> ParentFolders(string file)
    {
        for (var slash = file.LastIndexOf('/'); slash > 0; slash = file.LastIndexOf('/', slash - 1))
        {
            yield return file[..slash];
        }
    }
}
