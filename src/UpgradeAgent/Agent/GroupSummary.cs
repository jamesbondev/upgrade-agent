using System.Text.Json;
using System.Text.Json.Serialization;

namespace UpgradeAgent.Agent;

/// <summary>The agent's own account of a group. Informational: the app checks the claims against the diff.</summary>
public sealed record GroupSummary(IReadOnlyList<PackageSummary> Packages);

public sealed record PackageSummary(
    string Id,
    string From,
    string To,
    string Status,
    IReadOnlyList<string> BreakingChanges,
    IReadOnlyList<AppliedFix> Fixes,
    IReadOnlyList<string> UpcomingDeprecations,
    IReadOnlyList<string> Unresolved);

public sealed record AppliedFix(string File, string Reason);

public static class GroupSummaryParser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    public const string Schema = """
        {"packages":[{"id":"string","from":"string","to":"string","status":"fixed|no-changes-needed|unresolved",
          "breakingChanges":["string"],"fixes":[{"file":"repo-relative path","reason":"one line"}],
          "upcomingDeprecations":["string"],"unresolved":["error text and what was tried"]}]}
        """;

    /// <summary>Accepts bare JSON or JSON inside a Markdown code fence; returns null rather than throwing.</summary>
    public static GroupSummary? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var start = text.IndexOf('{', StringComparison.Ordinal);
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            var summary = JsonSerializer.Deserialize<GroupSummary>(text[start..(end + 1)], Options);
            return summary?.Packages is null ? null : Normalize(summary);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static GroupSummary Normalize(GroupSummary summary) => new(summary.Packages.Select(p => p with
    {
        BreakingChanges = p.BreakingChanges ?? [],
        Fixes = p.Fixes ?? [],
        UpcomingDeprecations = p.UpcomingDeprecations ?? [],
        Unresolved = p.Unresolved ?? [],
    }).ToList());
}
