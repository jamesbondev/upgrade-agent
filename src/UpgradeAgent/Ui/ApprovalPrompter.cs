using Spectre.Console;

namespace UpgradeAgent.Ui;

internal interface IApprovalPrompter
{
    /// <summary>Asks the operator. Implementations must return false rather than wait when nobody can answer.</summary>
    Task<bool> ConfirmAsync(string action, string reason, CancellationToken cancellationToken);
}

internal sealed class ConsoleApprovalPrompter(IAnsiConsole console, object consoleLock) : IApprovalPrompter
{
    public Task<bool> ConfirmAsync(string action, string reason, CancellationToken cancellationToken)
    {
        lock (consoleLock)
        {
            console.WriteLine();
            console.Write(new Panel(new Markup($"{Markup.Escape(action)}\n[grey]{Markup.Escape(reason)}[/]"))
                .Header("[yellow] approval needed [/]")
                .BorderColor(Color.Yellow));
            var approved = console.Confirm("Allow this?", defaultValue: false);
            console.MarkupLine(approved ? "  [green]approved by operator[/]" : "  [red]declined by operator[/]");
            return Task.FromResult(approved);
        }
    }
}

/// <summary>For pipelines and redirected output: anything that needs a human is declined, never left waiting.</summary>
internal sealed class DeclineAllPrompter : IApprovalPrompter
{
    public Task<bool> ConfirmAsync(string action, string reason, CancellationToken cancellationToken) => Task.FromResult(false);
}
