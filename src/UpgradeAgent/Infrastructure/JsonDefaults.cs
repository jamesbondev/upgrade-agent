using System.Text.Json;
using System.Text.Json.Serialization;

namespace UpgradeAgent.Infrastructure;

internal static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase), new NuGetVersionJsonConverter(), new NuGetFrameworkJsonConverter() },
    };

    public static readonly JsonSerializerOptions Compact = new(Options) { WriteIndented = false };
}
