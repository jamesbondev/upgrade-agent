using System.Xml.Linq;
using UpgradeAgent.Infrastructure;

namespace UpgradeAgent.Agent;

/// <summary>Migration material for one package version, found by the app so the agent never has to search the disk.</summary>
internal sealed record PackageDocs(string Id, string Version, string? PackageFolder, IReadOnlyList<string> DocFiles, string? ReleaseNotes, string? ProjectUrl, string? RepositoryUrl)
{
    /// <summary>Files that describe how to migrate, as opposed to general READMEs.</summary>
    public IEnumerable<string> MigrationFiles => DocFiles.Where(IsMigrationDoc);

    public static bool IsMigrationDoc(string path)
    {
        var name = Path.GetFileName(path).ToUpperInvariant();
        return name.StartsWith("MIGRATION", StringComparison.Ordinal) || name.StartsWith("BREAKING", StringComparison.Ordinal)
            || name.StartsWith("UPGRADING", StringComparison.Ordinal) || name.StartsWith("CHANGELOG", StringComparison.Ordinal)
            || name.StartsWith("CHANGES", StringComparison.Ordinal) || name.Contains("RELEASE", StringComparison.Ordinal);
    }
}

internal sealed class PackageDocsLocator(IProcessRunner processRunner)
{
    private static readonly string[] DocPatterns = ["MIGRATION*", "CHANGELOG*", "CHANGES*", "RELEASE*NOTES*", "BREAKING*", "UPGRADING*", "README*"];

    /// <summary>Resolved inside the worktree: a repo's nuget.config can move the global packages folder.</summary>
    public async Task<string?> GlobalPackagesFolderAsync(string worktree, CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync("dotnet", ["nuget", "locals", "global-packages", "--list"], worktree, cancellationToken: cancellationToken);
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

        var docs = DocPatterns
            .SelectMany(p => Directory.EnumerateFiles(folder, p + ".md", SearchOption.TopDirectoryOnly).Concat(Directory.EnumerateFiles(folder, p + ".txt", SearchOption.TopDirectoryOnly)))
            .Concat(Directory.Exists(Path.Combine(folder, "docs")) ? Directory.EnumerateFiles(Path.Combine(folder, "docs"), "*.md", SearchOption.AllDirectories) : [])
            .Distinct(StringComparer.Ordinal)
            .ToList();

        string? releaseNotes = null, projectUrl = null, repositoryUrl = null;
        var nuspec = Directory.EnumerateFiles(folder, "*.nuspec").FirstOrDefault();
        if (nuspec is not null)
        {
            try
            {
                var metadata = XDocument.Load(nuspec).Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "metadata");
                string? Element(string name) => metadata?.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() is { Length: > 0 } v ? v : null;
                releaseNotes = Element("releaseNotes");
                projectUrl = Element("projectUrl");
                repositoryUrl = (string?)metadata?.Elements().FirstOrDefault(e => e.Name.LocalName == "repository")?.Attribute("url");
            }
            catch (System.Xml.XmlException)
            {
                // A malformed nuspec just means fewer hints.
            }
        }

        return new PackageDocs(id, version, folder, docs, releaseNotes, projectUrl, repositoryUrl);
    }
}
