using System.Text.Json;
using System.Text.Json.Serialization;
using NuGet.Frameworks;
using NuGet.Versioning;

namespace UpgradeAgent.Infrastructure;

/// <summary>Versions travel as <see cref="NuGetVersion"/> and are written as normalized strings ("1.2.0").</summary>
internal sealed class NuGetVersionJsonConverter : JsonConverter<NuGetVersion>
{
    public override NuGetVersion Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        NuGetVersion.TryParse(reader.GetString(), out var version) ? version : throw new JsonException($"'{reader.GetString()}' is not a NuGet version.");

    public override void Write(Utf8JsonWriter writer, NuGetVersion value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToNormalizedString());
}

/// <summary>Target frameworks travel as <see cref="NuGetFramework"/> and are written as short folder names ("net10.0").</summary>
internal sealed class NuGetFrameworkJsonConverter : JsonConverter<NuGetFramework>
{
    public override NuGetFramework Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        NuGetFramework.Parse(reader.GetString() ?? throw new JsonException("A target framework can't be null."));

    public override void Write(Utf8JsonWriter writer, NuGetFramework value, JsonSerializerOptions options) => writer.WriteStringValue(value.GetShortFolderName());
}
