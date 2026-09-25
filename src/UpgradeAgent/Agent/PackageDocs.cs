using System.Xml;
using System.Xml.Linq;
using UpgradeAgent.Build;
using UpgradeAgent.Infrastructure;

namespace UpgradeAgent.Agent;

internal sealed record PackageDocs(string Id, string Version, string? PackageFolder, IReadOnlyList<string> DocFiles, string? ReleaseNotes, string? ProjectUrl, string? RepositoryUrl)
{
    public IEnumerable<string> MigrationFiles => DocFiles.Where(PackageDocsLocator.IsMigrationDoc);
}

internal sealed class PackageDocsLocator(IProcessRunner processRunner)
{
    private static readonly (string Pattern, bool IsMigration)[] DocKinds =
    [
        ("MIGRATION*", true), ("BREAKING*", true), ("UPGRADING*", true), ("CHANGELOG*", true), ("CHANGES*", true), ("RELEASE*NOTES*", true),
        ("README*", false),
    ];

    private static readonly string[] DocExtensions = [".md", ".txt"];

    public static bool IsMigrationDoc(string path) =>
        DocKinds.Any(k => k.IsMigration && Glob.IsMatch(k.Pattern + ".*", Path.GetFileName(path)));

    public async Task<string?> GlobalPackagesFolderAsync(string worktree, CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            "dotnet", ["nuget", "locals", "global-packages", "--list"], worktree, DotnetCli.BaseEnvironment, cancellationToken);
        if (!result.Succeeded)
        {
            return null;
        }

        var line = result.StandardOutput.Split('\n').FirstOrDefault(l => l.Contains("global-packages:", StringComparison.Ordinal));
        return line is null ? null : line[(line.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
    }

    public static PackageDocs Find(string? globalPackagesFolder, string id, string version)
    {
        var folder = globalPackagesFolder is null ? null : Path.Combine(globalPackagesFolder, id.ToLowerInvariant(), version.ToLowerInvariant());
        if (folder is null || !Directory.Exists(folder))
        {
            return new PackageDocs(id, version, null, [], null, null, null);
        }

        var docsFolder = Path.Combine(folder, "docs");
        var docs = DocKinds
            .SelectMany(k => DocExtensions.SelectMany(extension => Directory.EnumerateFiles(folder, k.Pattern + extension, SearchOption.TopDirectoryOnly)))
            .Concat(Directory.Exists(docsFolder) ? Directory.EnumerateFiles(docsFolder, "*.md", SearchOption.AllDirectories) : [])
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var (releaseNotes, projectUrl, repositoryUrl) = ReadNuspec(folder);
        return new PackageDocs(id, version, folder, docs, releaseNotes, projectUrl, repositoryUrl);
    }

    private static (string? ReleaseNotes, string? ProjectUrl, string? RepositoryUrl) ReadNuspec(string folder)
    {
        if (Directory.EnumerateFiles(folder, "*.nuspec").FirstOrDefault() is not { } nuspec)
        {
            return default;
        }

        try
        {
            var metadata = XDocument.Load(nuspec).Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata");
            string? Element(string name) => metadata?.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() is { Length: > 0 } v ? v : null;
            var repository = (string?)metadata?.Elements().FirstOrDefault(e => e.Name.LocalName == "repository")?.Attribute("url");
            return (Element("releaseNotes"), Element("projectUrl"), repository);
        }
        catch (XmlException)
        {
            return default;
        }
    }
}
