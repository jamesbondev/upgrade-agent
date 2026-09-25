using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentHarness.Tests;

public class StructuredOutputTests
{
    [Fact]
    public void SchemaForNamesThePropertiesAsTheyAppearInJson()
    {
        var schema = StructuredOutput.SchemaFor<Summary>();

        Assert.Contains("\"title\"", schema, StringComparison.Ordinal);
        Assert.Contains("\"changedFiles\"", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void SchemaForIncludesPropertyDescriptions()
    {
        var schema = StructuredOutput.SchemaFor<Summary>();

        Assert.Contains("One line, for a commit message.", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void SchemaForMarksRequiredProperties()
    {
        var schema = StructuredOutput.SchemaFor<Summary>();

        Assert.Contains("\"required\"", schema, StringComparison.Ordinal);
    }

    [Fact]
    public void PromptForAsksForJsonOnlyAndIncludesTheSchema()
    {
        var prompt = StructuredOutput.PromptFor<Summary>("Summarise your changes.");

        Assert.StartsWith("Summarise your changes.", prompt, StringComparison.Ordinal);
        Assert.Contains("ONLY a JSON object", prompt, StringComparison.Ordinal);
        Assert.Contains(StructuredOutput.SchemaFor<Summary>(), prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseReadsBareJson()
    {
        var reply = StructuredOutput.Parse<Summary>("""{"title":"Fix build","changedFiles":["a.cs"]}""");

        Assert.Null(reply.Error);
        Assert.Equal("Fix build", reply.Value?.Title);
        Assert.Equal(["a.cs"], reply.Value?.ChangedFiles);
    }

    [Fact]
    public void ParseReadsJsonInsideAMarkdownFence()
    {
        var reply = StructuredOutput.Parse<Summary>("""
            Here you go:
            ```json
            {"title":"Fix build"}
            ```
            """);

        Assert.Null(reply.Error);
        Assert.Equal("Fix build", reply.Value?.Title);
    }

    [Fact]
    public void ParseAllowsTrailingCommas()
    {
        var reply = StructuredOutput.Parse<Summary>("""{"title":"Fix build","changedFiles":["a.cs",],}""");

        Assert.Equal("Fix build", reply.Value?.Title);
    }

    [Fact]
    public void ParseSkipsComments()
    {
        var reply = StructuredOutput.Parse<Summary>("""
            {
              // the model explains itself
              "title": "Fix build" /* and again */
            }
            """);

        Assert.Equal("Fix build", reply.Value?.Title);
    }

    [Fact]
    public void ParseIgnoresExtraFields()
    {
        var reply = StructuredOutput.Parse<Summary>("""{"title":"Fix build","confidence":0.9}""");

        Assert.Null(reply.Error);
        Assert.Equal("Fix build", reply.Value?.Title);
    }

    [Fact]
    public void ParseMatchesPropertyNamesCaseInsensitively()
    {
        var reply = StructuredOutput.Parse<Summary>("""{"TITLE":"Fix build","ChangedFiles":["a.cs"]}""");

        Assert.Equal("Fix build", reply.Value?.Title);
        Assert.Equal(["a.cs"], reply.Value?.ChangedFiles);
    }

    [Fact]
    public void ParseReportsAMissingRequiredPropertyAsAnError()
    {
        var reply = StructuredOutput.Parse<Summary>("""{"changedFiles":["a.cs"]}""");

        Assert.Null(reply.Value);
        Assert.Contains(nameof(Summary), reply.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "empty")]
    [InlineData("   ", "empty")]
    [InlineData("I could not finish.", "no JSON object")]
    [InlineData("{\"title\": ", "no JSON object")]
    [InlineData("{\"title\": 42}", "doesn't match")]
    public void ParseNeverThrowsForABadReply(string? text, string expectedError)
    {
        var reply = StructuredOutput.Parse<Summary>(text);

        Assert.Null(reply.Value);
        Assert.Contains(expectedError, reply.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseKeepsTheRawTextEitherWay()
    {
        const string Text = "no json here";

        Assert.Equal(Text, StructuredOutput.Parse<Summary>(Text).Text);
    }

    [Fact]
    public void SchemaForListsEnumValuesByName()
    {
        var schema = StructuredOutput.SchemaFor<Verdict>();

        Assert.Contains("\"Current\"", schema, StringComparison.Ordinal);
        Assert.Contains("\"Stale\"", schema, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"state":"Stale"}""")]
    [InlineData("""{"state":"stale"}""")]
    [InlineData("""{"state":1}""")]
    public void ParseReadsEnumsByNameOrNumber(string text)
    {
        var reply = StructuredOutput.Parse<Verdict>(text);

        Assert.Null(reply.Error);
        Assert.Equal(State.Stale, reply.Value?.State);
    }

    [Fact]
    public void ParseReportsAnUnknownEnumName()
    {
        var reply = StructuredOutput.Parse<Verdict>("""{"state":"Ancient"}""");

        Assert.Null(reply.Value);
        Assert.Contains("doesn't match", reply.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEnumWithItsOwnConverterKeepsIt()
    {
        var reply = StructuredOutput.Parse<Labelled>("""{"level":"very-high"}""");

        Assert.Null(reply.Error);
        Assert.Equal(Level.VeryHigh, reply.Value?.Level);
    }

    public enum State
    {
        Current,
        Stale,
    }

    [JsonConverter(typeof(LevelConverter))]
    public enum Level
    {
        Low,
        VeryHigh,
    }

    public sealed class Labelled
    {
        public required Level Level { get; init; }
    }

    public sealed class LevelConverter : JsonConverter<Level>
    {
        public override Level Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.GetString() == "very-high" ? Level.VeryHigh : Level.Low;

        public override void Write(Utf8JsonWriter writer, Level value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value == Level.VeryHigh ? "very-high" : "low");
    }

    public sealed class Verdict
    {
        public required State State { get; init; }
    }

    public sealed class Summary
    {
        [Description("One line, for a commit message.")]
        public required string Title { get; init; }

        public IReadOnlyList<string> ChangedFiles { get; init; } = [];
    }
}
