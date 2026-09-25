using Spectre.Console;

namespace UpgradeAgent.Ui;

internal static class ConsoleFactory
{
    /// <summary>
    /// The default console. When output is redirected (CI logs, recordings) Spectre assumes 80 columns;
    /// a <c>COLUMNS</c> environment variable overrides that so tables don't wrap needlessly.
    /// </summary>
    public static IAnsiConsole Create()
    {
        var console = AnsiConsole.Console;
        if (Console.IsOutputRedirected && int.TryParse(Environment.GetEnvironmentVariable("COLUMNS"), out var width) && width >= 40)
        {
            console.Profile.Width = width;
        }

        return console;
    }
}
