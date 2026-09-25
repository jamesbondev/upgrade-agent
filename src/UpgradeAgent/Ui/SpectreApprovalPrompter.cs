using AgentHarness.Policies;
using Spectre.Console;

namespace UpgradeAgent.Ui;

/// <summary>
/// Asks at the console, through Spectre so the prompt doesn't tear the live output. Cancelling (Ctrl+C, or a
/// budget running out) counts as declining. Unattended runs use <see cref="ApprovalPrompter.DeclineAll"/> instead.
/// </summary>
internal sealed class SpectreApprovalPrompter(SynchronizedConsole console) : IApprovalPrompter
{
    public async Task<bool> ConfirmAsync(string action, string reason, CancellationToken cancellationToken)
    {
        try
        {
            return await console.ExclusiveAsync(c => AskAsync(c, action, reason, cancellationToken), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<bool> AskAsync(IAnsiConsole console, string action, string reason, CancellationToken cancellationToken)
    {
        console.WriteLine();
        console.Write(new Panel(new Markup($"{Markup.Escape(action)}\n[grey]{Markup.Escape(reason)}[/]"))
            .Header("[yellow] approval needed [/]")
            .BorderColor(Color.Yellow));

        var approved = await new ConfirmationPrompt("Allow this?") { DefaultValue = false }.ShowAsync(console, cancellationToken);
        console.MarkupLine(approved ? "  [green]approved by operator[/]" : "  [red]declined by operator[/]");
        return approved;
    }
}
