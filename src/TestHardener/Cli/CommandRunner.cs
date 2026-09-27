using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using RepoKit;
using RepoKit.AzureDevOps;
using Spectre.Console;
using TestHardener.Config;
using TestHardener.Stryker;
using TestHardener.Ui;

namespace TestHardener.Cli;

internal static class ExitCodes
{
    public const int Success = 0;
    public const int UnexpectedError = 1;
    public const int ConfigurationError = 2;
    public const int AllReposFailed = 3;
    public const int Cancelled = 130;
}

internal static class CommonOptions
{
    public static readonly Option<FileInfo> Config = new("--config")
    {
        Description = "JSON config file layered over appsettings.json.",
        Recursive = true,
    };

    public static readonly Option<bool> Verbose = new("--verbose")
    {
        Description = "Trace every external command (git, dotnet, Stryker) to stderr.",
        Recursive = true,
    };
}

internal static class CommandRunner
{
    public static async Task<int> RunAsync<THandler>(ParseResult parseResult, Func<THandler, Task<int>> run)
        where THandler : notnull
    {
        var console = ConsoleFactory.Create();
        try
        {
            await using var services = AppServices.Build(
                parseResult.GetValue(CommonOptions.Config)?.FullName, console, parseResult.GetValue(CommonOptions.Verbose));
            return await run(services.GetRequiredService<THandler>());
        }
        catch (Exception ex)
        {
            return Report(console, ex);
        }
    }

    internal static int Report(IAnsiConsole console, Exception exception)
    {
        switch (exception)
        {
            case ConfigurationException or OptionsValidationException
                or InvalidOperationException { Source: "Microsoft.Extensions.Configuration.Binder" }:
                console.MarkupLine($"[red]Configuration error:[/] {Markup.Escape(exception.Message)}");
                return ExitCodes.ConfigurationError;
            case AzureDevOpsAuthException:
                console.MarkupLine($"[red]Azure DevOps sign-in:[/] {Markup.Escape(exception.Message)}");
                return ExitCodes.ConfigurationError;
            case StrykerToolException:
                console.MarkupLine($"[red]Stryker:[/] {Markup.Escape(exception.Message)}");
                return ExitCodes.ConfigurationError;
            case ProcessStartException:
                console.MarkupLine($"[red]{Markup.Escape(exception.Message)}[/]");
                return ExitCodes.UnexpectedError;
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
