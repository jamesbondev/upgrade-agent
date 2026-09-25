using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentHarness;

namespace UpgradeAgent.Agent;

internal sealed record GroupSummary
{
    public required IReadOnlyList<PackageSummary> Packages { get; init; }
}

[JsonConverter(typeof(PackageStatusConverter))]
internal enum PackageStatus
{
    Fixed,
    NoChangesNeeded,
    Unresolved,
}

internal sealed class PackageStatusConverter : JsonConverter<PackageStatus>
{
    private static readonly Dictionary<string, PackageStatus> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fixed"] = PackageStatus.Fixed,
        ["no-changes-needed"] = PackageStatus.NoChangesNeeded,
        ["unresolved"] = PackageStatus.Unresolved,
    };

    public const string Names = "fixed, no-changes-needed or unresolved";

    public override PackageStatus Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.GetString() is { } name && ByName.TryGetValue(name, out var status)
            ? status
            : throw new JsonException($"Unknown package status '{reader.GetString()}'; expected {Names}.");

    public override void Write(Utf8JsonWriter writer, PackageStatus value, JsonSerializerOptions options) =>
        writer.WriteStringValue(ByName.First(n => n.Value == value).Key);
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

    [Description("One of: " + PackageStatusConverter.Names + ".")]
    public required PackageStatus Status { get; init; }

    [Description("Breaking changes in this update that affected this repository, one line each.")]
    public IReadOnlyList<string> BreakingChanges { get; init => field = value ?? []; } = [];

    public IReadOnlyList<AppliedFix> Fixes { get; init => field = value ?? []; } = [];

    [Description("Deprecation warnings left in place, one line each.")]
    public IReadOnlyList<string> UpcomingDeprecations { get; init => field = value ?? []; } = [];

    [Description("Errors still failing, with what was tried.")]
    public IReadOnlyList<string> Unresolved { get; init => field = value ?? []; } = [];
}

internal sealed record AppliedFix
{
    [Description("Repository-relative path of the changed file.")]
    public required string File { get; init; }

    [Description("What changed and why, in one line.")]
    public required string Reason { get; init; }
}

internal static class GroupSummaryParser
{
    public static string Schema { get; } = StructuredOutput.SchemaFor<GroupSummary>();

    public static GroupSummary? TryParse(string? text) => Validate(StructuredOutput.Parse<GroupSummary>(text).Value);

    public static GroupSummary? Validate(GroupSummary? summary) =>
        summary?.Packages is { } packages && packages.All(p => p is { Id.Length: > 0, From.Length: > 0, To.Length: > 0 }) ? summary : null;
}
