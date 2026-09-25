namespace AgentHarness.Policies;

/// <summary>Asks a human about an action a policy left to them (<see cref="ToolVerdict.Ask"/>).</summary>
public interface IApprovalPrompter
{
    /// <summary>
    /// Returns true to allow. Implementations must return false rather than wait when nobody can answer, and
    /// treat cancellation as declining. The session's time budget is paused while this runs.
    /// </summary>
    Task<bool> ConfirmAsync(string action, string reason, CancellationToken cancellationToken);
}

/// <summary>Ready-made prompters.</summary>
public static class ApprovalPrompter
{
    /// <summary>For pipelines and tests: anything that needs a human is declined, never left waiting. The default.</summary>
    public static IApprovalPrompter DeclineAll { get; } = From((_, _) => false);

    /// <summary>A y/N question on the console; declines when input is redirected.</summary>
    public static IApprovalPrompter Console { get; } = new ConsoleApprovalPrompter();

    public static IApprovalPrompter From(Func<string, string, bool> confirm) => new DelegatePrompter((a, r, _) => Task.FromResult(confirm(a, r)));

    public static IApprovalPrompter From(Func<string, string, CancellationToken, Task<bool>> confirm) => new DelegatePrompter(confirm);

    private sealed class DelegatePrompter(Func<string, string, CancellationToken, Task<bool>> confirm) : IApprovalPrompter
    {
        public Task<bool> ConfirmAsync(string action, string reason, CancellationToken cancellationToken) => confirm(action, reason, cancellationToken);
    }
}

/// <summary>Plain <see cref="System.Console"/> prompts, one at a time. Apps with a richer UI implement <see cref="IApprovalPrompter"/> themselves.</summary>
public sealed class ConsoleApprovalPrompter : IApprovalPrompter
{
    // The console is one per process, so prompts from every instance take turns.
    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    public async Task<bool> ConfirmAsync(string action, string reason, CancellationToken cancellationToken)
    {
        if (System.Console.IsInputRedirected)
        {
            System.Console.WriteLine($"  approval needed: {action} — declined (no interactive console)");
            return false;
        }

        try
        {
            await OneAtATime.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        try
        {
            System.Console.WriteLine();
            System.Console.WriteLine($"  approval needed: {action}");
            System.Console.WriteLine($"  ({reason})");
            System.Console.Write("  Allow this? [y/N] ");
            var answer = await System.Console.In.ReadLineAsync(cancellationToken);
            var approved = answer?.Trim() is "y" or "Y" or "yes" or "Yes";
            System.Console.WriteLine(approved ? "  approved" : "  declined");
            return approved;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            OneAtATime.Release();
        }
    }
}
