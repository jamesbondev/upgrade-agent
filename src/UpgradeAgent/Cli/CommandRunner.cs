using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Spectre.Console;
using UpgradeAgent.Config;
using UpgradeAgent.Detection;
using UpgradeAgent.Infrastructure;
using UpgradeAgent.Run;
using UpgradeAgent.Ui;

namespace UpgradeAgent.Cli;

internal static class ExitCodes
{
    public const int Success = 0;
    public const int UnexpectedError = 1;
    public const int ConfigurationError = 2;
    public const int PreflightFailed = 3;
    public const int DetectionFailed = 4;
    public const int RunAborted = 5;
    public const int Cancelled = 130;
}

/// <summary>What every command shares: the console, the composed services, and one mapping from failures to exit codes.</summary>
internal static class CommandRunner
{
    public static async Task<int> RunAsync<THandler>(ParseResult parseResult, Func<THandler, Task<int>> run, bool interactive = true)
        where THandler : notnull
    {
        var console = ConsoleFactory.Create();
        try
        {
            await using var services = AppServices.Build(
                parseResult.GetValue(CommonOptions.Config)?.FullName, console, interactive, parseResult.GetValue(CommonOptions.Verbose));
            return await run(services.GetRequiredService<THandler>());
        }
        catch (Exception ex)
        {
            return Report(console, ex);
        }
    }

    private static int Report(IAnsiConsole console, Exception exception)
    {
        switch (exception)
        {
            // The binder reports a value it can't convert (Agent:Provider = "bogus") as InvalidOperationException.
            case ConfigurationException or OptionsValidationException
                or InvalidOperationException { Source: "Microsoft.Extensions.Configuration.Binder" }:
                console.MarkupLine($"[red]Configuration error:[/] {Markup.Escape(exception.Message)}");
                return ExitCodes.ConfigurationError;
            case PackageListException packageList:
                console.MarkupLine($"[red]{Markup.Escape(packageList.Message)}[/]");
                console.WriteLine(packageList.RawOutput.Trim());
                return ExitCodes.DetectionFailed;
            case RunAbortedException:
                console.MarkupLine($"[red]Run aborted:[/] {Markup.Escape(exception.Message)}");
                return ExitCodes.RunAborted;
            case GitException:
                console.MarkupLine($"[red]git failed:[/] {Markup.Escape(exception.Message)}");
                return ExitCodes.RunAborted;
            case ProcessStartException:
                console.MarkupLine($"[red]{Markup.Escape(exception.Message)}[/]");
                return ExitCodes.RunAborted;
            case OperationCanceledException:
                console.MarkupLine("[yellow]Cancelled.[/]");
                return ExitCodes.Cancelled;
            default:
                console.MarkupLine($"[red]Unexpected error:[/] {Markup.Escape(exception.Message)}");
                console.MarkupLine("[grey]Run again with --verbose for a trace of external commands.[/]");
                return ExitCodes.UnexpectedError;
        }
    }
}
