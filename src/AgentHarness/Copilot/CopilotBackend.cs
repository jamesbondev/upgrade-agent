using System.Text.Json;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Extensions.AI;

namespace AgentHarness.Copilot;

public sealed record CopilotStatus(bool Ready, string? Login, string Message);

public sealed class CopilotBackend(CopilotOptions? options = null) : IAgentBackend
{
    private readonly CopilotOptions _options = options ?? new CopilotOptions();
    private readonly SemaphoreSlim _starting = new(1, 1);
    private CopilotClient? _client;
    private string? _clientDirectory;

    public string Name => "GitHub Copilot";

    public string? Model => _options.Model;

    public async Task<CopilotStatus> CheckAsync(string? workingDirectory = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = await GetClientAsync(workingDirectory ?? Environment.CurrentDirectory, cancellationToken);
            var status = await client.GetAuthStatusAsync(cancellationToken);
            return status.IsAuthenticated
                ? new CopilotStatus(true, status.Login, $"signed in to Copilot as {status.Login} ({status.AuthType})")
                : new CopilotStatus(false, null, $"Copilot is not signed in{(string.IsNullOrWhiteSpace(status.StatusMessage) ? "" : $" ({status.StatusMessage})")}. {HowToSignIn}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new CopilotStatus(false, null, $"The Copilot runtime didn't start: {ex.GetBaseException().Message}. {HowToSignIn}");
        }
    }

    public async Task<IAgentBackendSession> StartSessionAsync(AgentBackendSettings settings, CancellationToken cancellationToken)
    {
        var client = await GetClientAsync(settings.WorkingDirectory, cancellationToken);
        var config = HardenedConfig(settings.WorkingDirectory);
        config.Streaming = true;
        config.SystemMessage = new SystemMessageConfig { Mode = SystemMessageMode.Append, Content = settings.Instructions };
        config.ExcludedTools = settings.AllowWebFetch ? [.. _options.ExcludedTools] : [.. _options.ExcludedTools, CopilotToolNames.WebFetch];
        if (settings.Tools.Count > 0)
        {
            config.Tools = [.. settings.Tools.Select(t => t.RequiresApproval ? new ApprovalRequiredAIFunction(t.Function) : t.Function)];
        }

        if (!settings.UseBuiltInTools)
        {
            config.AvailableTools = [.. settings.Tools.Select(t => t.Name)];
        }

        config.OnPermissionRequest = async (request, _) =>
        {
            var permission = await settings.AuthorizeAsync(ToToolRequest(request), CancellationToken.None);
            return permission.Allowed ? PermissionDecision.ApproveOnce() : PermissionDecision.Reject(permission.Feedback ?? "Not allowed.");
        };
        config.OnEvent = sessionEvent =>
        {
            if (ToAgentEvent(sessionEvent) is { } agentEvent)
            {
                settings.OnEvent(agentEvent);
            }
        };
        _options.ConfigureSession?.Invoke(config);
        GateApprovalRequiredTools(config);

        return new SingleCopilotSession(await client.CreateSessionAsync(config, cancellationToken));
    }

    public async ValueTask DisposeAsync()
    {
        await StopClientAsync();
        _starting.Dispose();
    }

    internal static ToolRequest ToToolRequest(PermissionRequest request) => request switch
    {
        PermissionRequestShell shell => new ShellRequest(shell.FullCommandText, shell.HasWriteFileRedirection, [.. shell.PossiblePaths ?? []]),
        PermissionRequestWrite write => new FileWriteRequest(write.FileName),
        PermissionRequestRead read => new FileReadRequest(read.Path),
        PermissionRequestUrl url => new WebFetchRequest(url.Url),
        PermissionRequestCustomTool custom => new CustomToolRequest(custom.ToolName, custom.Args?.GetRawText()),
        PermissionRequestMcp mcp => new McpToolRequest(mcp.ServerName, mcp.ToolName, mcp.Args?.GetRawText(), mcp.ReadOnly),
        PermissionRequestHook { ToolName: { Length: > 0 } tool } hook => new CustomToolRequest(tool, hook.ToolArgs?.GetRawText()),
        _ => new OtherToolRequest($"{request.Kind}"),
    };

    private const string HowToSignIn =
        "Install the GitHub Copilot CLI, run 'copilot' and then '/login'; or, in a pipeline, set CopilotOptions.GitHubTokenEnvironmentVariable.";

    private SessionConfig HardenedConfig(string workingDirectory) => new()
    {
        ClientName = _options.ClientName,
        Model = _options.Model,
        ReasoningEffort = _options.ReasoningEffort,
        WorkingDirectory = workingDirectory,
        EnableConfigDiscovery = false,
        EnableFileHooks = false,
        EnableSkills = false,
        SkipCustomInstructions = true,
        EnableOnDemandInstructionDiscovery = false,
        EnableHostGitOperations = false,
    };

    private async Task<CopilotClient> GetClientAsync(string workingDirectory, CancellationToken cancellationToken)
    {
        await _starting.WaitAsync(cancellationToken);
        try
        {
            if (_client is not null && _clientDirectory == workingDirectory)
            {
                return _client;
            }

            await StopClientAsync();
            var tokenVariable = string.IsNullOrWhiteSpace(_options.GitHubTokenEnvironmentVariable) ? null : _options.GitHubTokenEnvironmentVariable;
            var client = new CopilotClient(new CopilotClientOptions
            {
                WorkingDirectory = workingDirectory,
                Environment = AgentEnvironment.Build(
                    Environment.GetEnvironmentVariables(),
                    tokenVariable is null ? _options.HiddenEnvironmentVariables : [.. _options.HiddenEnvironmentVariables, tokenVariable],
                    _options.EnvironmentOverrides.AsReadOnly()),
                GitHubToken = tokenVariable is null ? null : Environment.GetEnvironmentVariable(tokenVariable),
            });
            try
            {
                await client.StartAsync(cancellationToken);
            }
            catch
            {
                await client.DisposeAsync();
                throw;
            }

            _client = client;
            _clientDirectory = workingDirectory;
            return client;
        }
        finally
        {
            _starting.Release();
        }
    }

    private async Task StopClientAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
            _client = null;
            _clientDirectory = null;
        }
    }

    private static AgentEvent? ToAgentEvent(SessionEvent sessionEvent) => sessionEvent switch
    {
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

    internal static void GateApprovalRequiredTools(SessionConfig config)
    {
        var gated = (config.Tools ?? [])
            .OfType<AIFunction>()
            .Where(f => f.GetService<ApprovalRequiredAIFunction>() is not null)
            .Select(f => f.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (gated.Count == 0 || config.Hooks?.OnPreToolUse is not null)
        {
            return;
        }

        config.Hooks ??= new SessionHooks();
        config.Hooks.OnPreToolUse = (input, _) => Task.FromResult(gated.Contains(input.ToolName)
            ? new PreToolUseHookOutput { PermissionDecision = "ask", PermissionDecisionReason = $"Tool '{input.ToolName}' requires approval." }
            : null);
    }

    private sealed class SingleCopilotSession(CopilotSession session) : IAgentBackendSession
    {
        public async Task<string?> SendAsync(string message, CancellationToken cancellationToken)
        {
            var replies = new List<string>();
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var subscription = session.On<SessionEvent>(sessionEvent =>
            {
                switch (sessionEvent)
                {
                    case AssistantMessageEvent { Data.ParentToolCallId: null } reply when !string.IsNullOrEmpty(reply.Data.Content):
                        lock (replies)
                        {
                            replies.Add(reply.Data.Content);
                        }

                        break;
                    case SessionIdleEvent:
                        done.TrySetResult();
                        break;
                    case SessionErrorEvent error:
                        done.TrySetException(new InvalidOperationException($"Session error: {error.Data?.Message ?? "Unknown error"}"));
                        break;
                }
            });
            using var cancellation = cancellationToken.Register(() => done.TrySetCanceled(cancellationToken));

            await session.SendAsync(new MessageOptions { Prompt = message }, cancellationToken);
            await done.Task;
            lock (replies)
            {
                return replies.Count == 0 ? null : string.Join(Environment.NewLine, replies);
            }
        }

        public ValueTask DisposeAsync() => session.DisposeAsync();
    }
}
