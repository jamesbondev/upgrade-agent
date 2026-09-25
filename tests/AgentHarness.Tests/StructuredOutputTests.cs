using System.ComponentModel;

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

    public sealed class Summary
    {
        [Description("One line, for a commit message.")]
        public required string Title { get; init; }

        public IReadOnlyList<string> ChangedFiles { get; init; } = [];
    }
}
