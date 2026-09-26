using ReadmeChecker.Detection;

namespace ReadmeChecker.Tests.Detection;

public class CommandRulesTests
{
    [Fact]
    public void CdChangesToItsFirstArgument()
    {
        var reading = CommandRules.Read(["cd", "src/App"]);

        Assert.Equal(1, reading.ChangesDirectoryTo);
        Assert.True(reading.ChecksOtherTokens);
    }

    [Fact]
    public void MkdirCreatesItsArgumentsAndChecksNothingElse()
    {
        string[] tokens = ["mkdir", "-p", "out/logs", "tmp"];

        var reading = CommandRules.Read(tokens);

        Assert.Equal(["out/logs", "tmp"], reading.Creates.Select(i => tokens[i]));
        Assert.False(reading.ChecksOtherTokens);
    }

    [Fact]
    public void DotnetNewCreatesItsOutputAndNameAndHasNoProjectTarget()
    {
        string[] tokens = ["dotnet", "new", "console", "-o", "src/NewApp", "--name", "NewApp"];

        var reading = CommandRules.Read(tokens);

        Assert.Equal(["src/NewApp", "NewApp"], reading.Creates.Select(i => tokens[i]));
        Assert.Empty(reading.Targets);
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
    public void AnUnknownCommandConsumesNothingAndChecksEveryToken()
    {
        var reading = CommandRules.Read(["cat", "src/a.json"]);

        Assert.False(reading.Consumes(0) || reading.Consumes(1));
        Assert.True(reading.ChecksOtherTokens);
        Assert.False(reading.LeavesRepository);
    }

    [Fact]
    public void AnyOtherDotnetVerbIsNotAPath()
    {
        var reading = CommandRules.Read(["dotnet", "./tools/app.dll"]);

        Assert.True(reading.Consumes(1));
        Assert.Empty(reading.Targets);
    }
}
