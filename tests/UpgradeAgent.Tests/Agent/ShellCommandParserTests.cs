using UpgradeAgent.Agent;

namespace UpgradeAgent.Tests.Agent;

public class ShellCommandParserTests
{
    [Fact]
    public void SplitsSegmentsAndRespectsQuotes()
    {
        Assert.True(ShellCommandParser.TryParse("dotnet test x.slnx --filter \"Name=a;b|c\" && grep -n 'x && y' f.cs | head -5", out var parsed, out _));

        Assert.Equal(3, parsed.Segments.Count);
        Assert.Equal(["dotnet", "test", "x.slnx", "--filter", "Name=a;b|c"], parsed.Segments[0]);
        Assert.Equal(["grep", "-n", "x && y", "f.cs"], parsed.Segments[1]);
    }

    [Fact]
    public void AllowsStderrMergeButNotOtherRedirection()
    {
        Assert.True(ShellCommandParser.TryParse("dotnet build x --no-restore 2>&1", out _, out _));
        Assert.False(ShellCommandParser.TryParse("dotnet build x --no-restore 2>err.txt", out _, out _));
    }

    [Theory]
    [InlineData("echo \"$(whoami)\"")]
    [InlineData("echo 'unbalanced")]
    [InlineData("ls\nrm -rf /")]
    [InlineData("cat $HOME/.ssh/id_rsa")]
    [InlineData("cat \"${HOME}/.ssh/id_rsa\"")]
    [InlineData("Get-Content $env:USERPROFILE\\.nuget\\NuGet.Config")]
    [InlineData("cat (Remove-Item x)")]
    [InlineData("ls @(1,2)")]
    [InlineData("(cd .. && ls)")]
    public void RefusesWhatItCannotReasonAbout(string command)
    {
        Assert.False(ShellCommandParser.TryParse(command, out _, out var error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("grep '$(x)' a.cs", "$(x)")]
    [InlineData("grep '$HOME' a.cs", "$HOME")]
    [InlineData("grep \"cost: $5\" a.cs", "cost: $5")]
    public void LiteralDollarSignsAreFine(string command, string argument)
    {
        Assert.True(ShellCommandParser.TryParse(command, out var parsed, out _));
        Assert.Equal(argument, parsed.Segments[0][1]);
    }
}
