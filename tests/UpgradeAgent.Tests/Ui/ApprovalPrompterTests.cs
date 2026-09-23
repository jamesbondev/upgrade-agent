using Spectre.Console.Testing;
using UpgradeAgent.Ui;

namespace UpgradeAgent.Tests.Ui;

public class ApprovalPrompterTests
{
    [Theory]
    [InlineData("y", true)]
    [InlineData("n", false)]
    public async Task ShowsTheActionAndReasonAndReturnsTheAnswer(string answer, bool expected)
    {
        var console = new TestConsole().Interactive();
        console.Input.PushTextWithEnter(answer);

        var approved = await new ConsoleApprovalPrompter(console, new object())
            .ConfirmAsync("edit src/App/App.csproj", "edit to a non-source file: src/App/App.csproj", CancellationToken.None);

        Assert.Equal(expected, approved);
        Assert.Contains("edit src/App/App.csproj", console.Output, StringComparison.Ordinal);
        Assert.Contains("approval needed", console.Output, StringComparison.Ordinal);
        Assert.Contains(expected ? "approved by operator" : "declined by operator", console.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnterAloneDeclines()
    {
        var console = new TestConsole().Interactive();
        console.Input.PushKey(ConsoleKey.Enter);

        Assert.False(await new ConsoleApprovalPrompter(console, new object()).ConfirmAsync("rm src/Old.cs", "file operation: rm", CancellationToken.None));
    }

    [Fact]
    public async Task NonInteractivePrompterDeclinesWithoutWaiting()
    {
        Assert.False(await new DeclineAllPrompter().ConfirmAsync("anything", "reason", CancellationToken.None));
    }
}
