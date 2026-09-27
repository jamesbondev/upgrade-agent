using AgentHarness.Copilot;

namespace AgentHarness.Tests;

public class FileAgentLogTests
{
    [Fact]
    public async Task Observer_AppendsTimestampedEventsAndNotes()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("fal-").FullName, "nested", "agent.log");

        await using (var log = FileAgentLog.Open(path))
        {
            log.Observer.OnEvent(new SessionStopped("done"));
            log.Note("a note");
        }

        var lines = await File.ReadAllLinesAsync(path);
        Assert.Matches(@"^\d\d:\d\d:\d\d\.\d{3} .*done", lines[0]);
        Assert.Equal("a note", lines[1]);
    }

    [Fact]
    public async Task Open_AppendsToAnExistingLog()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("fal-").FullName, "agent.log");
        await File.WriteAllTextAsync(path, "earlier\n");

        await using (var log = FileAgentLog.Open(path))
        {
            log.Note("later");
        }

        Assert.Equal(["earlier", "later"], await File.ReadAllLinesAsync(path));
    }
}

public class CopilotQuotaTests
{
    [Theory]
    [InlineData("You have exceeded your monthly quota of premium requests", true)]
    [InlineData("Error: Quota exceeded for model", true)]
    [InlineData("rate limited", false)]
    [InlineData(null, false)]
    public void IsExceeded_RecognisesQuotaMessages(string? message, bool expected) =>
        Assert.Equal(expected, CopilotQuota.IsExceeded(message));
}
