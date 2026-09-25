namespace AgentHarness.Policies;

public interface IApprovalPrompter
{
    Task<bool> ConfirmAsync(string action, string reason, CancellationToken cancellationToken);
}

public static class ApprovalPrompter
{
    public static IApprovalPrompter DeclineAll { get; } = From((_, _) => false);

    public static IApprovalPrompter Console { get; } = new ConsoleApprovalPrompter();

    public static IApprovalPrompter From(Func<string, string, bool> confirm) => new DelegatePrompter((a, r, _) => Task.FromResult(confirm(a, r)));

    public static IApprovalPrompter From(Func<string, string, CancellationToken, Task<bool>> confirm) => new DelegatePrompter(confirm);

    private sealed class DelegatePrompter(Func<string, string, CancellationToken, Task<bool>> confirm) : IApprovalPrompter
    {
        public Task<bool> ConfirmAsync(string action, string reason, CancellationToken cancellationToken) => confirm(action, reason, cancellationToken);
    }
}

public sealed class ConsoleApprovalPrompter : IApprovalPrompter
{
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
