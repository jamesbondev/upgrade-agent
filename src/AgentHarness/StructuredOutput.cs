using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace AgentHarness;

public sealed record StructuredReply<T>(T? Value, string? Text, string? Error)
    where T : class;

public static class StructuredOutput
{
    public static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    public static string SchemaFor<T>() => AIJsonUtilities.CreateJsonSchema(typeof(T), serializerOptions: SerializerOptions).GetRawText();

    public static string PromptFor<T>(string question) => $"""
        {question}
        Reply with ONLY a JSON object, with no prose and no code fence. Do not call any tools.
        It must match this JSON schema: {SchemaFor<T>()}
        """;

    public static StructuredReply<T> Parse<T>(string? text)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new(null, text, "the reply was empty");
        }

        var start = text.IndexOf('{', StringComparison.Ordinal);
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return new(null, text, "the reply has no JSON object");
        }

        try
        {
            var value = JsonSerializer.Deserialize<T>(text[start..(end + 1)], SerializerOptions);
            return new(value, text, value is null ? "the reply was JSON null" : null);
        }
        catch (JsonException ex)
        {
            return new(null, text, $"the JSON doesn't match {typeof(T).Name}: {ex.Message}");
        }
    }
}
