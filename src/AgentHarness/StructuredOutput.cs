using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace AgentHarness;

/// <summary>A reply that should have been JSON: the parsed value when it was, and the raw text either way.</summary>
/// <param name="Error">Why <paramref name="Value"/> is null: no reply, no JSON, or JSON that doesn't fit the type.</param>
public sealed record StructuredReply<T>(T? Value, string? Text, string? Error)
    where T : class;

/// <summary>
/// Structured replies from a model: the JSON schema is generated from your C# type, so the prompt and the parser
/// can't drift apart. Use <see cref="System.ComponentModel.DescriptionAttribute"/> on properties to guide the model,
/// and <c>required</c> for fields it must fill.
/// </summary>
public static class StructuredOutput
{
    /// <summary>Lenient about what models get wrong (case, trailing commas, comments, extra fields); strict about required fields.</summary>
    public static JsonSerializerOptions SerializerOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
    };

    public static string SchemaFor<T>() => AIJsonUtilities.CreateJsonSchema(typeof(T), serializerOptions: SerializerOptions).GetRawText();

    /// <summary>The instruction appended to a question so the model replies with JSON only.</summary>
    public static string PromptFor<T>(string question) => $"""
        {question}
        Reply with ONLY a JSON object, with no prose and no code fence. Do not call any tools.
        It must match this JSON schema: {SchemaFor<T>()}
        """;

    /// <summary>
    /// Accepts bare JSON or JSON inside a Markdown code fence. Never throws for a bad reply: the error says what
    /// was wrong, so a missing summary can be a note rather than a failure.
    /// </summary>
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
