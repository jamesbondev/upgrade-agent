using UpgradeAgent.Agent;

namespace UpgradeAgent.Tests.Agent;

public class ShellCommandsTests
{
    [Theory]
    [InlineData("dotnet build App.slnx --no-restore", true, false)]
    [InlineData("dotnet build App.slnx --no-restore 2>&1 | tail -20", true, false)]
    [InlineData("dotnet test App.slnx --no-build", false, true)]
    [InlineData("grep -rn Format src", false, false)]
    public void RecognisesBuildsAndTests(string command, bool isBuild, bool isTest)
    {
        Assert.Equal((isBuild, isTest), (ShellCommands.IsBuild(command), ShellCommands.IsTest(command)));
    }
}
