using ReadmeChecker.Detection;

namespace ReadmeChecker.Tests.Detection;

public class ShellLineTests
{
    [Theory]
    [InlineData("dotnet build", "dotnet,build")]
    [InlineData("$ ./build.sh --fast", "./build.sh,--fast")]
    [InlineData("PS C:\\repo> ./build.ps1", "./build.ps1")]
    [InlineData("./a.sh && ./b.sh || c ; d | e", "./a.sh|./b.sh|c|d|e")]
    [InlineData("cat 'a b.json' \"c d\"", "cat,a b.json,c d")]
    [InlineData("./build.sh # then src/x.json", "./build.sh")]
    [InlineData("# ./build.sh", "")]
    [InlineData("// ./build.sh", "")]
    [InlineData("REM build.cmd", "")]
    [InlineData("   ", "")]
    public void ALineSplitsIntoCommandsOfTokens(string line, string expected) =>
        Assert.Equal(
            expected.Length == 0 ? [] : expected.Split('|'),
            ShellLine.Commands(line).Select(tokens => string.Join(',', tokens)));
}
