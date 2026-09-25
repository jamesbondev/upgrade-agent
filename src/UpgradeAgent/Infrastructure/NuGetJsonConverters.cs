using System.Text.Json;
using System.Text.Json.Serialization;
using NuGet.Frameworks;
using NuGet.Versioning;

namespace UpgradeAgent.Infrastructure;

internal sealed class NuGetVersionJsonConverter : JsonConverter<NuGetVersion>
{
    public override NuGetVersion Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        NuGetVersion.TryParse(reader.GetString(), out var version) ? version : throw new JsonException($"'{reader.GetString()}' is not a NuGet version.");

    public override void Write(Utf8JsonWriter writer, NuGetVersion value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToNormalizedString());
}

internal sealed class NuGetFrameworkJsonConverter : JsonConverter<NuGetFramework>
{
    public override NuGetFramework Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        NuGetFramework.Parse(reader.GetString() ?? throw new JsonException("A target framework can't be null."));

    public override void Write(Utf8JsonWriter writer, NuGetFramework value, JsonSerializerOptions options) => writer.WriteStringValue(value.GetShortFolderName());
}
