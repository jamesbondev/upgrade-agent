using AgentHarness.Policies;
using Spectre.Console;

namespace TestHardener.Ui;

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

internal sealed class SpectreApprovalPrompter(IAnsiConsole console) : IApprovalPrompter
{
    public async Task<bool> ConfirmAsync(string action, string reason, CancellationToken cancellationToken)
    {
        console.MarkupLine($"  [yellow]approval needed:[/] {Markup.Escape(action)} [grey]({Markup.Escape(reason)})[/]");
        try
        {
            return await new ConfirmationPrompt("  Go ahead?") { DefaultValue = false }.ShowAsync(console, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
