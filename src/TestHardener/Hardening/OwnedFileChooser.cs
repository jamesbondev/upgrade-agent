using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TestHardener.Analysis;
using TestHardener.Config;

namespace TestHardener.Hardening;

internal sealed record OwnedFile(string Path, string TestProject, bool IsNew, string Reason);

internal static class OwnedFileChooser
{
    public static OwnedFile? Choose(SurvivorGroup group, TargetConfig target, IReadOnlyCollection<string> trackedFiles, Func<string, string?> read)
    {
        var typeName = group.Member.OuterType;
        if (typeName.Length == 0 || target.TestProjects.Count == 0)
        {
            return null;
        }

        var tracked = trackedFiles.ToHashSet(StringComparer.Ordinal);
        foreach (var file in group.CoveringTestFiles)
        {
            if (tracked.Contains(file) && ProjectOf(file, target) is { } project)
            {
                var count = group.CoveringTests.Count;
                return new OwnedFile(file, project, IsNew: false, $"it holds the tests that cover {group.Member.Name} ({count} covering tests)");
            }
        }

        var mirrored = MirroredFolder(group.File, target);
        var reference = new Regex($@"\b{Regex.Escape(typeName)}\b", RegexOptions.None, TimeSpan.FromSeconds(1));
        var referencing = tracked
            .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && ProjectOf(f, target) is not null)
            .Select(f => (File: f, Text: read(f)))
            .Where(f => f.Text is not null && reference.IsMatch(f.Text))
            .OrderByDescending(f => FolderOf(f.File, ProjectOf(f.File, target)!) == mirrored)
            .ThenByDescending(f => System.IO.Path.GetFileName(f.File).Equals($"{typeName}Tests.cs", StringComparison.Ordinal))
            .ThenByDescending(f => reference.Count(f.Text!))
            .ThenBy(f => f.File, StringComparer.Ordinal)
            .FirstOrDefault();
        if (referencing.File is { } existing)
        {
            return new OwnedFile(existing, ProjectOf(existing, target)!, IsNew: false, $"it already tests {typeName}");
        }

        var firstProject = target.TestProjects[0];
        var path = Combine(ProjectFolder(firstProject), mirrored, $"{typeName}Tests.cs");
        return tracked.Contains(path)
            ? new OwnedFile(path, firstProject, IsNew: false, "it's at the path that mirrors the source file")
            : new OwnedFile(path, firstProject, IsNew: true, $"no test file references {typeName} yet");
    }

    public static string Skeleton(OwnedFile owned, string? projectFileText, string? sourceText, string typeName)
    {
        var rootNamespace = RootNamespace(projectFileText) ?? System.IO.Path.GetFileNameWithoutExtension(owned.TestProject);
        var folder = FolderOf(owned.Path, owned.TestProject);
        var ns = string.Join('.', new[] { rootNamespace }.Concat(folder.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Identifier)));
        var sourceNamespace = sourceText is null
            ? null
            : CSharpSyntaxTree.ParseText(sourceText).GetCompilationUnitRoot().DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();
        var usingLine = sourceNamespace is null || sourceNamespace == ns || ns.StartsWith(sourceNamespace + ".", StringComparison.Ordinal)
            ? ""
            : $"using {sourceNamespace};\n\n";
        return $"{usingLine}namespace {ns};\n\npublic class {typeName}Tests\n{{\n}}\n";
    }

    internal static string? ProjectOf(string file, TargetConfig target) =>
        target.TestProjects.FirstOrDefault(p => ProjectFolder(p) is var folder && (folder.Length == 0
            ? !file.StartsWith(ProjectFolder(target.Project) + "/", StringComparison.Ordinal)
            : file.StartsWith(folder + "/", StringComparison.Ordinal)));

    internal static string MirroredFolder(string sourceFile, TargetConfig target) => FolderOf(sourceFile, target.Project);

    private static string FolderOf(string file, string project)
    {
        var projectFolder = ProjectFolder(project);
        var relative = projectFolder.Length == 0 ? file : file.StartsWith(projectFolder + "/", StringComparison.Ordinal) ? file[(projectFolder.Length + 1)..] : file;
        var slash = relative.LastIndexOf('/');
        return slash < 0 ? "" : relative[..slash];
    }

    private static string ProjectFolder(string project)
    {
        var slash = project.LastIndexOf('/');
        return slash < 0 ? "" : project[..slash];
    }

    private static string Combine(params string[] parts) => string.Join('/', parts.Where(p => p.Length > 0));

    private static string Identifier(string segment)
    {
        var cleaned = new string(segment.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
        return cleaned.Length > 0 && char.IsDigit(cleaned[0]) ? "_" + cleaned : cleaned;
    }

    private static string? RootNamespace(string? projectFileText)
    {
        if (projectFileText is null)
        {
            return null;
        }

        try
        {
            using var reader = XmlReader.Create(new StringReader(projectFileText), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var value = XDocument.Load(reader).Descendants().FirstOrDefault(e => e.Name.LocalName == "RootNamespace")?.Value.Trim();
            return string.IsNullOrEmpty(value) ? null : value;
        }
        catch (XmlException)
        {
            return null;
        }
    }
}
