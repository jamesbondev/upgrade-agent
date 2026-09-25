using AgentHarness.Testing;
using Microsoft.Extensions.Time.Testing;

namespace AgentHarness.Tests.TestSupport;

internal static class ScriptedTurnExtensions
{
    public static ScriptedTurn Elapse(this ScriptedTurn turn, FakeTimeProvider time, TimeSpan duration) => turn.Step((_, _) =>
    {
        time.Advance(duration);
        return Task.CompletedTask;
    });

    public static ScriptedTurn Authorize(this ScriptedTurn turn, ToolRequest request) => turn.Step((c, ct) => c.AuthorizeAsync(request, ct));
}
