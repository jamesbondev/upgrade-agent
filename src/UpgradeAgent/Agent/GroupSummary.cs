using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace UpgradeAgent.Agent;

/// <summary>The agent's own account of a group. Informational: the app checks the claims against the diff.</summary>
internal sealed record GroupSummary
{
    public required IReadOnlyList<PackageSummary> Packages { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter<PackageStatus>))]
internal enum PackageStatus
{
    [JsonStringEnumMemberName("fixed")]
    Fixed,

    [JsonStringEnumMemberName("no-changes-needed")]
    NoChangesNeeded,

    [JsonStringEnumMemberName("unresolved")]
    Unresolved,
}

internal static class PackageStatusExtensions
{
    public static string Label(this PackageStatus status) => status switch
    {
        PackageStatus.Fixed => "fixed",
        PackageStatus.NoChangesNeeded => "no changes needed",
        _ => "unresolved",
    };
}

internal sealed record PackageSummary
{
    [Description("NuGet package ID.")]
    public required string Id { get; init; }

    [Description("Version before the update.")]
    public required string From { get; init; }

    [Description("Version after the update.")]
    public required string To { get; init; }

    public required PackageStatus Status { get; init; }

    [Description("Breaking changes in this update that affected this repository, one line each.")]
    public IReadOnlyList<string> BreakingChanges { get; init; } = [];

    public IReadOnlyList<AppliedFix> Fixes { get; init; } = [];

    [Description("Deprecation warnings left in place, one line each.")]
    public IReadOnlyList<string> UpcomingDeprecations { get; init; } = [];

    [Description("Errors still failing, with what was tried.")]
    public IReadOnlyList<string> Unresolved { get; init; } = [];
}

internal sealed record AppliedFix
{
    [Description("Repository-relative path of the changed file.")]
    public required string File { get; init; }

    [Description("What changed and why, in one line.")]
    public required string Reason { get; init; }
}

/// <summary>
/// The contract for the summary turn. The JSON schema sent to the model is generated from the C# types above,
/// so the prompt and the parser can't drift apart.
/// </summary>
internal static class GroupSummaryParser
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    public static string Schema { get; } = AIJsonUtilities.CreateJsonSchema(typeof(GroupSummary), serializerOptions: Options).GetRawText();

    /// <summary>
    /// Accepts bare JSON or JSON inside a Markdown code fence. Returns null rather than throwing when the reply
    /// isn't a valid summary (missing required fields included): a summary is useful, never essential.
    /// </summary>
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
            return JsonSerializer.Deserialize<GroupSummary>(text[start..(end + 1)], Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
