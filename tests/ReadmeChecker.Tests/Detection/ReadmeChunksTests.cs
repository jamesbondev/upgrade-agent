using ReadmeChecker.Detection;

namespace ReadmeChecker.Tests.Detection;

public class ReadmeChunksTests
{
    [Fact]
    public void SectionsArePackedIntoPartsOfAboutEightyLines()
    {
        var readme = Sections(("# Title", 10), ("## A", 30), ("## B", 30), ("## C", 30), ("## D", 5));

        var plan = ReadmeChunks.Plan(readme);

        Assert.Equal([(1, 70), (71, 105)], plan.Chunks.Select(c => (c.FirstLine, c.LastLine)));
        Assert.Equal(["Title", "A", "B"], plan.Chunks[0].Headings);
        Assert.Equal(["# Title", "## A", "## B", "## C", "## D"], plan.Outline);
        Assert.Empty(plan.Unchecked);
    }

    [Fact]
    public void AnOversizedSectionIsNeverSplitInsideACodeBlockOrAList()
    {
        var body = new List<string> { "## Big" };
        body.AddRange(Enumerable.Range(0, 70).Select(i => $"text {i}"));
        body.Add("");
        body.Add("```");
        body.AddRange(Enumerable.Range(0, 20).Select(i => i == 10 ? "" : $"code {i}"));
        body.Add("```");
        body.Add("");
        body.AddRange(Enumerable.Range(0, 30).Select(i => $"- item {i}"));
        body.Add("");
        body.AddRange(Enumerable.Range(0, 60).Select(i => $"more {i}"));
        var readme = string.Join('\n', body);
        var lines = readme.Split('\n');

        var plan = ReadmeChunks.Plan(readme);

        Assert.True(plan.Chunks.Count > 1);
        foreach (var chunk in plan.Chunks.Skip(1))
        {
            var first = lines[chunk.FirstLine - 1];
            Assert.Equal("", lines[chunk.FirstLine - 2]);
            Assert.False(first.StartsWith("code", StringComparison.Ordinal), $"part starts inside the code block: {first}");
            Assert.False(first.StartsWith("- item", StringComparison.Ordinal) && first != "- item 0", $"part starts inside the list: {first}");
        }

        Assert.Equal(lines.Length, plan.Chunks[^1].LastLine);
    }

    [Fact]
    public void AReadmeWithoutHeadingsIsOnePartWhenShort()
    {
        var plan = ReadmeChunks.Plan("Just a paragraph.\n\nAnother one.");

        var chunk = Assert.Single(plan.Chunks);
        Assert.Equal((1, 3), (chunk.FirstLine, chunk.LastLine));
    }

    [Fact]
    public void PartsPastTheCapAreReportedAsUnchecked()
    {
        var readme = Sections(Enumerable.Range(0, 12).Select(i => ($"## S{i}", 75)).ToArray());

        var plan = ReadmeChunks.Plan(readme);

        Assert.Equal(ReadmeChunks.MaxChunks, plan.Chunks.Count);
        Assert.Equal(12 - ReadmeChunks.MaxChunks, plan.Unchecked.Count);
        Assert.Equal(plan.Chunks[^1].LastLine + 1, plan.Unchecked[0].FirstLine);
    }

    private static string Sections(params (string Heading, int Lines)[] sections) =>
        string.Join('\n', sections.SelectMany(s => new[] { s.Heading }.Concat(Enumerable.Range(1, s.Lines - 1).Select(i => $"{s.Heading.TrimStart('#', ' ')} line {i}"))));
}
