using ReadmeChecker.Detection;

namespace ReadmeChecker.Tests.Detection;

public class CommandRulesTests
{
    [Fact]
    public void CdChangesToItsFirstArgument()
    {
        var reading = CommandRules.Read(["cd", "src/App"]);

        Assert.Equal(1, reading.ChangesDirectory);
        Assert.Equal([0], reading.CommandWords);
    }

    [Fact]
    public void MkdirCreatesItsArgumentsAndChecksNothingElse()
    {
        var reading = CommandRules.Read(["mkdir", "-p", "out/logs", "tmp"]);

        Assert.Equal(["out/logs", "tmp"], reading.Creates);
        Assert.False(reading.ChecksOtherTokens);
    }

    [Fact]
    public void DotnetNewCreatesItsOutputAndName()
    {
        var reading = CommandRules.Read(["dotnet", "new", "console", "-o", "src/NewApp", "--name", "NewApp"]);

        Assert.Equal(["src/NewApp", "NewApp"], reading.Creates);
        Assert.False(reading.ChecksOtherTokens);
    }

    [Theory]
    [InlineData("dotnet run --project src/App", "3:Project")]
    [InlineData("dotnet test src/App.Tests -c Release", "2:Project")]
    [InlineData("dotnet build -c Release", "")]
    [InlineData("dotnet run --project src/App -- --project x", "3:Project")]
    [InlineData("dotnet tool restore", "")]
    [InlineData("./build.sh", "0:Script")]
    [InlineData("pwsh -File scripts/build.ps1", "2:Script")]
    [InlineData("pwsh -c Get-Thing", "")]
    public void CommandTargetsAreTheArgumentsTheCommandRuns(string line, string expected) =>
        Assert.Equal(
            expected.Length == 0 ? [] : expected.Split(' '),
            CommandRules.Read(line.Split(' ')).Targets.Select(t => $"{t.Index}:{t.Role}"));

    [Fact]
    public void GitCloneLeavesTheRepository() =>
        Assert.True(CommandRules.Read(["git", "clone", "https://example.com/x.git"]).LeavesRepository);

    [Fact]
    public void AnUnknownCommandChecksEveryToken() =>
        Assert.Same(CommandReading.Unknown, CommandRules.Read(["cat", "src/a.json"]));

    [Fact]
    public void TheFirstMatchingRuleWins()
    {
        var reading = CommandRules.Read(["dotnet", "./tools/app.dll"]);

        Assert.Equal([0, 1], reading.CommandWords);
        Assert.Empty(reading.Targets);
    }
}
