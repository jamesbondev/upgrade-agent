using System.Text.Json;

namespace UpgradeAgent.Detection;

/// <summary>One top-level package reference as reported by <c>dotnet package list --outdated --format json</c>.</summary>
/// <param name="RequestedVersion">What the project asks for; null when the CLI doesn't report it.</param>
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

    /// <summary>
    /// Parses JSON output (format version 1). Projects with nothing outdated have no <c>frameworks</c>
    /// property. When a feed is unreachable the CLI prints plain text instead of JSON; that is an error, and so
    /// is JSON that doesn't match the schema (a changed CLI).
    /// </summary>
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

    // The CLI's JSON (format version 1). Missing lists are empty; missing required values are a schema change.
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

        /// <summary>Usually a string, but not guaranteed: kept raw.</summary>
        public JsonElement? Text { get; init; }
    }
}
