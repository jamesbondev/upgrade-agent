using AgentHarness.Testing;
using Microsoft.Extensions.Time.Testing;

namespace AgentHarness.Tests.TestSupport;

internal static class ScriptedTurnExtensions
{
    /// <summary>Moves fake time forward, as if the agent spent that long on this step.</summary>
    public static ScriptedTurn Elapse(this ScriptedTurn turn, FakeTimeProvider time, TimeSpan duration) => turn.Step((_, _) =>
    {
        time.Advance(duration);
        return Task.CompletedTask;
    });

    /// <summary>Asks the session about <paramref name="request"/>, as a runtime would before running a tool.</summary>
    public static ScriptedTurn Authorize(this ScriptedTurn turn, ToolRequest request) => turn.Step((c, ct) => c.AuthorizeAsync(request, ct));
}
