namespace AgentHarness;

/// <summary>
/// Prints a session's activity to the console, one line per event: good enough to watch an agent work while
/// you build on the harness. Apps with a richer UI write their own <see cref="IAgentObserver"/>.
/// </summary>
/// <param name="workingDirectory">Paths under it are shown relative to it.</param>
public sealed class ConsoleAgentObserver(string? workingDirectory = null, TextWriter? output = null) : IAgentObserver
{
    private readonly TextWriter _output = output ?? Console.Out;
    private bool _quiet;

    public void OnEvent(AgentEvent agentEvent)
    {
        if (agentEvent is UserMessage user)
        {
            // Replies to tools-off turns are data (JSON): the app prints what it wants of them.
            _quiet = user.WithoutTools;
            return;
        }

        var line = agentEvent switch
        {
            ToolCallStarted { Kind: ToolKind.Shell } call => $"    $ {call.Detail}",
            ToolCallStarted { Kind: ToolKind.Edit } call => $"    ✎ {call.Detail}",
            ToolCallStarted call => $"    · {call.Tool} {(call.Detail.Length > 0 ? call.Detail : call.RawArguments is null or "{}" ? "" : Shorten(call.RawArguments))}".TrimEnd(),
            ToolCallCompleted { Success: false } failed => $"      ✗ {Shorten(failed.Error ?? "failed")}",
            AssistantMessage message when !_quiet && FirstLine(message.Text) is { } text => $"  {Shorten(text)}",
            ToolRefused refused => $"    ⊘ refused {refused.Action}: {refused.Reason}",
            ToolApprovedByOperator approved => $"    ✓ approved {approved.Action}",
            ModelServed { IsFallback: true } served => $"  warning: asked for {served.Requested} but the provider is serving {served.Model}",
            ModelServed served => $"  model: {served.Model}",
            SessionStopped stopped => $"  {stopped.Reason}",
            _ => null,
        };

        if (line is not null)
        {
            _output.WriteLine(workingDirectory is null ? line : PathText.Relative(workingDirectory, line));
        }
    }

    private static string? FirstLine(string text) => text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);

    private static string Shorten(string text) => text.Length <= 140 ? text : text[..139] + "…";
}
