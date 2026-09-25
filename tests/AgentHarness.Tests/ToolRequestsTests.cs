namespace AgentHarness.Tests;

public class ToolRequestsTests
{
    [Theory]
    [MemberData(nameof(Descriptions))]
    public void DescribeIsAOneLineSummary(ToolRequest request, string expected)
    {
        Assert.Equal(expected, request.Describe());
    }

    public static TheoryData<ToolRequest, string> Descriptions => new()
    {
        { new ShellRequest("dotnet build"), "dotnet build" },
        { new FileWriteRequest("src/a.cs"), "edit src/a.cs" },
        { new FileReadRequest("src/a.cs"), "read src/a.cs" },
        { new WebFetchRequest("https://example.com"), "fetch https://example.com" },
        { new CustomToolRequest("deploy", "{\"env\":\"prod\"}"), "deploy {\"env\":\"prod\"}" },
        { new CustomToolRequest("deploy", "{}"), "deploy" },
        { new CustomToolRequest("deploy"), "deploy" },
        { new OtherToolRequest("mcp"), "mcp" },
    };
}
