using System.Text.Json;

namespace UpgradeAgent.Detection;

/// <summary>One top-level package reference as reported by <c>dotnet package list --outdated --format json</c>.</summary>
public sealed record ReportedPackage(
    string ProjectPath,
    string Framework,
    string Id,
    string RequestedVersion,
    string ResolvedVersion,
    string? LatestVersion);

public sealed class PackageListException(string message, string rawOutput, Exception? inner = null) : Exception(message, inner)
{
    public string RawOutput { get; } = rawOutput;
}

public static class PackageListParser
{
    /// <summary>
    /// Parses JSON output (format version 1). Projects with nothing outdated have no <c>frameworks</c>
    /// property. When a feed is unreachable the CLI prints plain text instead of JSON; that is an error.
    /// </summary>
    public static IReadOnlyList<ReportedPackage> Parse(string output)
    {
        if (!output.TrimStart().StartsWith('{'))
        {
            throw new PackageListException("dotnet package list did not return JSON (often a feed or authentication problem).", output);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(output);
        }
        catch (JsonException ex)
        {
            throw new PackageListException("dotnet package list returned malformed JSON.", output, ex);
        }

        using (document)
        {
            var root = document.RootElement;
            ThrowOnProblems(root, output);

            var packages = new List<ReportedPackage>();
            if (!root.TryGetProperty("projects", out var projects))
            {
                return packages;
            }

            foreach (var project in projects.EnumerateArray())
            {
                var projectPath = project.GetProperty("path").GetString()!;
                if (!project.TryGetProperty("frameworks", out var frameworks))
                {
                    continue;
                }

                foreach (var framework in frameworks.EnumerateArray())
                {
                    var frameworkName = framework.GetProperty("framework").GetString()!;
                    if (!framework.TryGetProperty("topLevelPackages", out var topLevel))
                    {
                        continue;
                    }

                    foreach (var package in topLevel.EnumerateArray())
                    {
                        packages.Add(new ReportedPackage(
                            projectPath,
                            frameworkName,
                            package.GetProperty("id").GetString()!,
                            GetString(package, "requestedVersion") ?? "",
                            GetString(package, "resolvedVersion") ?? "",
                            GetString(package, "latestVersion")));
                    }
                }
            }

            return packages;
        }
    }

    private static void ThrowOnProblems(JsonElement root, string output)
    {
        if (!root.TryGetProperty("problems", out var problems) || problems.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var errors = problems.EnumerateArray()
            .Where(p => string.Equals(GetString(p, "level"), "error", StringComparison.OrdinalIgnoreCase))
            .Select(p => GetString(p, "text") ?? p.ToString())
            .ToList();

        if (errors.Count > 0)
        {
            throw new PackageListException($"dotnet package list reported errors: {string.Join("; ", errors)}", output);
        }
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
