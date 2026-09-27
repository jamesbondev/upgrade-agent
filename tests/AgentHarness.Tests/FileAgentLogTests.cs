namespace AgentHarness.Tests;

public class FileAgentLogTests
{
    [Fact]
    public async Task Observer_AppendsTimestampedEventsAndNotes()
    {
        var folder = Directory.CreateTempSubdirectory("fal-").FullName;
        var path = Path.Combine(folder, "nested", "agent.log");

        await using (var log = FileAgentLog.Open(path))
        {
            log.Observer.OnEvent(new SessionStopped("done"));
            log.Note("a note");
        }

        var lines = await File.ReadAllLinesAsync(path);
        Directory.Delete(folder, recursive: true);
        Assert.Matches(@"^\d\d:\d\d:\d\d\.\d{3} .*done", lines[0]);
        Assert.Equal("a note", lines[1]);
    }

    [Fact]
    public async Task Open_AppendsToAnExistingLog()
    {
        var folder = Directory.CreateTempSubdirectory("fal-").FullName;
        var path = Path.Combine(folder, "agent.log");
        await File.WriteAllTextAsync(path, "earlier\n");

        await using (var log = FileAgentLog.Open(path))
        {
            log.Note("later");
        }

        var lines = await File.ReadAllLinesAsync(path);
        Directory.Delete(folder, recursive: true);
        Assert.Equal(["earlier", "later"], lines);
    }
}
