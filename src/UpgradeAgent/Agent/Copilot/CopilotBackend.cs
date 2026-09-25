using System.Text.Json;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.GitHub.Copilot;
using UpgradeAgent.Agent.Activities;
using UpgradeAgent.Config;

namespace UpgradeAgent.Agent.Copilot;

/// <summary>
/// GitHub Copilot through Agent Framework's <see cref="GitHubCopilotAgent"/> over the local Copilot CLI login.
/// Copilot owns the tool loop; this class only maps its permission requests and session events to the
/// app's neutral types, and back.
/// </summary>
internal sealed class CopilotBackend(CopilotClientHost host, AgentOptions options) : IAgentBackend
{
    public string Name => "GitHub Copilot";

    public async Task<IAgentSession> StartSessionAsync(AgentSessionSettings settings, CancellationToken cancellationToken)
    {
        var client = await host.GetAsync(settings.WorkingDirectory, cancellationToken);
        var config = CopilotSessions.Hardened(options, settings.WorkingDirectory);
        config.Streaming = true;
        config.ExcludedTools = settings.AllowWebFetch ? [.. CopilotToolNames.Excluded] : [.. CopilotToolNames.Excluded, CopilotToolNames.WebFetch];
        config.SystemMessage = new SystemMessageConfig { Mode = SystemMessageMode.Append, Content = settings.SystemPrompt };
        config.OnPermissionRequest = async (request, _) =>
        {
            var permission = await settings.AuthorizeAsync(ToToolRequest(request), cancellationToken);
            return permission.Allowed ? PermissionDecision.ApproveOnce() : PermissionDecision.Reject(permission.Feedback ?? "Not allowed.");
        };
        config.OnEvent = sessionEvent =>
        {
            if (ToAgentEvent(sessionEvent) is { } agentEvent)
            {
                settings.OnEvent(agentEvent);
            }
        };

        var agent = new GitHubCopilotAgent(client, config, ownsClient: false, name: CopilotSessions.ClientName);
        try
        {
            return new CopilotSession(agent, await agent.CreateSessionAsync(cancellationToken));
        }
        catch
        {
            await agent.DisposeAsync();
            throw;
        }
    }

    public ValueTask DisposeAsync() => host.DisposeAsync();

    internal static ToolRequest ToToolRequest(PermissionRequest request) => request switch
    {
        PermissionRequestShell shell => new ShellRequest(shell.FullCommandText, shell.HasWriteFileRedirection, [.. shell.PossiblePaths ?? []]),
        PermissionRequestWrite write => new FileWriteRequest(write.FileName),
        PermissionRequestRead read => new FileReadRequest(read.Path),
        PermissionRequestUrl url => new WebFetchRequest(url.Url),
        _ => new OtherToolRequest($"{request.Kind}"),
    };

    private static AgentEvent? ToAgentEvent(SessionEvent sessionEvent) => sessionEvent switch
    {
        // Sub-agent activity (a parent tool call) is the sub-agent's business; only top-level calls count.
        ToolExecutionStartEvent start when start.Data.ParentToolCallId is null => new ToolCallStarted(
            start.Data.ToolCallId,
            KindOf(start.Data.ToolName),
            start.Data.ToolName,
            DetailOf(start.Data.ToolName, start.Data.Arguments, start.Data.ShellToolInfo?.DisplayCommand),
            start.Data.Arguments?.GetRawText()),
        ToolExecutionCompleteEvent complete => new ToolCallCompleted(
            complete.Data.ToolCallId, complete.Data.Success, complete.Data.Result?.Content, complete.Data.Error?.Message),
        AssistantMessageEvent message when message.Data.ParentToolCallId is null => new AssistantMessage(message.Data.Content ?? ""),
        AssistantUsageEvent usage => new ModelUsage(
            usage.Data.Model, usage.Data.InputTokens ?? 0, usage.Data.OutputTokens ?? 0, (usage.Data.CopilotUsage?.TotalNanoAiu ?? 0) / 1e9),
        _ => null,
    };

    private static ToolKind KindOf(string tool) => tool switch
    {
        CopilotToolNames.Bash or CopilotToolNames.PowerShell => ToolKind.Shell,
        CopilotToolNames.Edit or CopilotToolNames.Create => ToolKind.Edit,
        CopilotToolNames.View or CopilotToolNames.Grep or CopilotToolNames.Glob or CopilotToolNames.ReadBash or CopilotToolNames.ListBash => ToolKind.Read,
        _ => ToolKind.Other,
    };

    private static string DetailOf(string tool, JsonElement? arguments, string? shellCommand) => tool switch
    {
        CopilotToolNames.Bash or CopilotToolNames.PowerShell => shellCommand ?? Argument(arguments, "command") ?? "",
        CopilotToolNames.View or CopilotToolNames.Create or CopilotToolNames.Edit => Argument(arguments, "path") ?? "",
        CopilotToolNames.Grep => $"{Argument(arguments, "pattern")} {Argument(arguments, "path")}".Trim(),
        CopilotToolNames.Glob => Argument(arguments, "pattern") ?? "",
        _ => "",
    };

    private static string? Argument(JsonElement? arguments, string name) =>
        arguments is { ValueKind: JsonValueKind.Object } a && a.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private sealed class CopilotSession(AIAgent agent, AgentSession session) : IAgentSession
    {
        public async Task<string?> SendAsync(string message, CancellationToken cancellationToken) =>
            (await agent.RunAsync(message, session, cancellationToken: cancellationToken)).Text;

        public ValueTask DisposeAsync() => agent is IAsyncDisposable disposable ? disposable.DisposeAsync() : ValueTask.CompletedTask;
    }
}
