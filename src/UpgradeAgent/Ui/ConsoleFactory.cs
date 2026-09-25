using Spectre.Console;

namespace UpgradeAgent.Ui;

internal static class ConsoleFactory
{
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
