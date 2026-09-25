using System.Text.Json;

namespace UpgradeAgent.Detection;

internal sealed record ReportedPackage(
    string ProjectPath,
    string Framework,
    string Id,
    string? RequestedVersion,
    string? ResolvedVersion,
    string? LatestVersion);

internal sealed class PackageListException(string message, string rawOutput, Exception? inner = null) : Exception(message, inner)
{
    public string RawOutput { get; } = rawOutput;
}

internal static class PackageListParser
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { RespectNullableAnnotations = true };

    public static IReadOnlyList<ReportedPackage> Parse(string output)
    {
        if (!output.TrimStart().StartsWith('{'))
        {
            throw new PackageListException("dotnet package list did not return JSON (often a feed or authentication problem).", output);
        }

        PackageListDocument document;
        try
        {
            document = JsonSerializer.Deserialize<PackageListDocument>(output, Options)
                ?? throw new PackageListException("dotnet package list returned empty JSON.", output);
        }
        catch (JsonException ex)
        {
            throw new PackageListException($"dotnet package list returned JSON this version doesn't understand: {ex.Message}", output, ex);
        }

        var errors = document.Problems
            .Where(p => string.Equals(p.Level, "error", StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Text is { ValueKind: JsonValueKind.String } text ? text.GetString()! : p.Text?.GetRawText() ?? "(no details)")
            .ToList();
        if (errors.Count > 0)
        {
            throw new PackageListException($"dotnet package list reported errors: {string.Join("; ", errors)}", output);
        }

        return document.Projects
            .SelectMany(project => project.Frameworks.SelectMany(framework => framework.TopLevelPackages.Select(package => new ReportedPackage(
                project.Path, framework.Framework, package.Id, package.RequestedVersion, package.ResolvedVersion, package.LatestVersion))))
            .ToList();
    }

    private sealed class PackageListDocument
    {
        public IReadOnlyList<ProjectEntry> Projects { get; init; } = [];

        public IReadOnlyList<Problem> Problems { get; init; } = [];
    }

    private sealed class ProjectEntry
    {
        public required string Path { get; init; }

        public IReadOnlyList<FrameworkEntry> Frameworks { get; init; } = [];
    }

    private sealed class FrameworkEntry
    {
        public required string Framework { get; init; }

        public IReadOnlyList<PackageEntry> TopLevelPackages { get; init; } = [];
    }

    private sealed class PackageEntry
    {
        public required string Id { get; init; }

        public string? RequestedVersion { get; init; }

        public string? ResolvedVersion { get; init; }

        public string? LatestVersion { get; init; }
    }

    private sealed class Problem
    {
        public string? Level { get; init; }

        public JsonElement? Text { get; init; }
    }
}
