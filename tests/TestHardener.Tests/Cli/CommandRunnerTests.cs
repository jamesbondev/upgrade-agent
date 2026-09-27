using Microsoft.Extensions.Options;
using RepoKit.AzureDevOps;
using Spectre.Console.Testing;
using TestHardener.Cli;
using TestHardener.Config;
using TestHardener.Hardening;
using TestHardener.Stryker;

namespace TestHardener.Tests.Cli;

public class CommandRunnerTests
{
    public static TheoryData<Exception, int, string> Cases() => new()
    {
        { new ConfigurationException("bad"), ExitCodes.ConfigurationError, "Configuration error" },
        { new OptionsValidationException("x", typeof(TestHardenerOptions), ["bad"]), ExitCodes.ConfigurationError, "Configuration error" },
        { new AzureDevOpsAuthException("no pat"), ExitCodes.ConfigurationError, "Azure DevOps sign-in" },
        { new StrykerToolException("no network"), ExitCodes.ConfigurationError, "Stryker" },
        { new AgentUnavailableException("quota"), ExitCodes.AgentUnavailable, "Copilot isn't ready" },
        { new OperationCanceledException(), ExitCodes.Cancelled, "Cancelled" },
        { new InvalidOperationException("oops"), ExitCodes.UnexpectedError, "Unexpected error" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Report_MapsExceptionsToExitCodes(Exception exception, int code, string message)
    {
        var console = new TestConsole();

        Assert.Equal(code, CommandRunner.Report(console, exception));
        Assert.Contains(message, console.Output, StringComparison.Ordinal);
    }
}
