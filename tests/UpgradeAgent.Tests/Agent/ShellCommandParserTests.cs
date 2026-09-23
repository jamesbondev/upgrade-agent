using UpgradeAgent.Agent;

namespace UpgradeAgent.Tests.Agent;

public class ShellCommandParserTests
{
    [Fact]
    public void SplitsSegmentsAndRespectsQuotes()
    {
        var parsed = ShellCommandParser.Parse("dotnet test x.slnx --filter \"Name=a;b|c\" && grep -n 'x && y' f.cs | head -5", out var error);

        Assert.Null(error);
        Assert.Equal(3, parsed.Segments.Count);
        Assert.Equal(["dotnet", "test", "x.slnx", "--filter", "Name=a;b|c"], parsed.Segments[0]);
        Assert.Equal(["grep", "-n", "x && y", "f.cs"], parsed.Segments[1]);
    }

    [Fact]
    public void AllowsStderrMergeButNotOtherRedirection()
    {
        ShellCommandParser.Parse("dotnet build x --no-restore 2>&1", out var merge);
        ShellCommandParser.Parse("dotnet build x --no-restore 2>err.txt", out var redirect);

        Assert.Null(merge);
        Assert.NotNull(redirect);
    }

    [Theory]
    [InlineData("echo \"$(whoami)\"")]
    [InlineData("echo 'unbalanced")]
    [InlineData("ls\nrm -rf /")]
    public void RefusesWhatItCannotReasonAbout(string command)
    {
        ShellCommandParser.Parse(command, out var error);

        Assert.NotNull(error);
    }

    [Fact]
    public void SingleQuotesKeepDollarSignsLiteral()
    {
        var parsed = ShellCommandParser.Parse("grep '$(x)' a.cs", out var error);

        Assert.Null(error);
        Assert.Equal("$(x)", parsed.Segments[0][1]);
    }
}
