using System.Text.Json;
using Microsoft.Extensions.AI;

namespace AgentHarness.Testing;

public sealed record ScriptedDecision(ToolRequest Request, bool Allowed, string? Feedback);

public sealed class ScriptedBackend(string? model = "scripted-model") : IAgentBackend
{
    private readonly Queue<ScriptedTurn> _turns = new();
    private readonly List<ScriptedDecision> _decisions = [];
    private readonly List<string> _messages = [];

    public string Name => "Scripted";

    public string? Model => model;

    public IReadOnlyList<ScriptedDecision> Decisions
    {
        get
        {
            lock (_decisions)
            {
                return [.. _decisions];
            }
        }
    }

    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (_messages)
            {
                return [.. _messages];
            }
        }
    }

    public AgentBackendSettings? LastSettings { get; private set; }

    public ScriptedBackend Turn(Action<ScriptedTurn> script)
    {
        var turn = new ScriptedTurn();
        script(turn);
        _turns.Enqueue(turn);
        return this;
    }

    public ScriptedBackend Reply(string text) => Turn(t => t.Reply(text));

    public Task<IAgentBackendSession> StartSessionAsync(AgentBackendSettings settings, CancellationToken cancellationToken)
    {
        LastSettings = settings;
        return Task.FromResult<IAgentBackendSession>(new Session(this, settings));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private void Record(ScriptedDecision decision)
    {
        lock (_decisions)
        {
            _decisions.Add(decision);
        }
    }

    private sealed class Session(ScriptedBackend owner, AgentBackendSettings settings) : IAgentBackendSession
    {
        private int _calls;

        public string? Model => owner.Model;

        public async Task<string?> SendAsync(string message, CancellationToken cancellationToken)
        {
            lock (owner._messages)
            {
                owner._messages.Add(message);
            }

            if (!owner._turns.TryDequeue(out var turn))
            {
                throw new InvalidOperationException($"The script has no turn left for this message: {message[..Math.Min(message.Length, 80)]}");
            }

            string? reply = null;
            foreach (var step in turn.Steps)
            {
                cancellationToken.ThrowIfCancellationRequested();
                reply = await step(new ScriptedContext(this, settings), cancellationToken) ?? reply;
            }

            return reply;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        internal string NextCallId() => $"call-{Interlocked.Increment(ref _calls)}";

        internal async Task<bool> AuthorizeAsync(ToolRequest request, CancellationToken cancellationToken)
        {
            var permission = await settings.AuthorizeAsync(request, cancellationToken);
            owner.Record(new ScriptedDecision(request, permission.Allowed, permission.Feedback));
            return permission.Allowed;
        }
    }

    public sealed class ScriptedContext
    {
        private readonly Session _session;

        internal ScriptedContext(object session, AgentBackendSettings settings)
        {
            _session = (Session)session;
            Settings = settings;
        }

        public AgentBackendSettings Settings { get; }

        public string? Model => _session.Model;

        public string NextCallId() => _session.NextCallId();

        public Task<bool> AuthorizeAsync(ToolRequest request, CancellationToken cancellationToken) => _session.AuthorizeAsync(request, cancellationToken);

        public void Raise(AgentEvent agentEvent) => Settings.OnEvent(agentEvent);

        public string FullPath(string path) => Path.GetFullPath(path, Settings.WorkingDirectory);

        public async Task RunToolAsync(ToolRequest request, ToolKind kind, string tool, string detail, Func<Task<(bool Success, string? Output)>> run, CancellationToken cancellationToken)
        {
            if (!await AuthorizeAsync(request, cancellationToken))
            {
                return;
            }

            var id = NextCallId();
            Raise(new ToolCallStarted(id, kind, tool, detail));
            var (success, output) = await run();
            Raise(new ToolCallCompleted(id, success, success ? output : null, success ? null : output));
        }
    }
}

public sealed class ScriptedTurn
{
    private readonly List<Func<ScriptedBackend.ScriptedContext, CancellationToken, Task<string?>>> _steps = [];

    internal IReadOnlyList<Func<ScriptedBackend.ScriptedContext, CancellationToken, Task<string?>>> Steps => _steps;

    public ScriptedTurn Shell(string command, string output = "", bool success = true, bool writesFile = false) => Step(async (c, ct) =>
        await c.RunToolAsync(new ShellRequest(command, writesFile), ToolKind.Shell, "bash", command, () => Task.FromResult((success, (string?)output)), ct));

    public ScriptedTurn Edit(string path, string? content = null) => Step(async (c, ct) =>
    {
        var full = c.FullPath(path);
        await c.RunToolAsync(new FileWriteRequest(full), ToolKind.Edit, "edit", full, async () =>
        {
            if (content is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                await File.WriteAllTextAsync(full, content, ct);
            }

            return (true, (string?)"edited");
        }, ct);
    });

    public ScriptedTurn Read(string path) => Step(async (c, ct) =>
    {
        var full = c.FullPath(path);
        await c.RunToolAsync(new FileReadRequest(full), ToolKind.Read, "view", full, async () =>
            File.Exists(full) ? (true, await File.ReadAllTextAsync(full, ct)) : (false, $"{path} does not exist"), ct);
    });

    public ScriptedTurn Fetch(string url, string content = "") => Step(async (c, ct) =>
        await c.RunToolAsync(new WebFetchRequest(url), ToolKind.Other, "web_fetch", url, () => Task.FromResult((true, (string?)content)), ct));

    public ScriptedTurn CallTool(string name, object? arguments = null) => Step(async (c, ct) =>
    {
        var json = JsonSerializer.Serialize(arguments ?? new { }, StructuredOutput.SerializerOptions);
        var tool = c.Settings.Tools.FirstOrDefault(t => t.Name == name);
        if (tool is null)
        {
            var id = c.NextCallId();
            c.Raise(new ToolCallStarted(id, ToolKind.Other, name, "", json));
            c.Raise(new ToolCallCompleted(id, false, null, $"Tool '{name}' does not exist."));
            return;
        }

        if (!await c.AuthorizeAsync(new CustomToolRequest(name, json), ct))
        {
            return;
        }

        var callId = c.NextCallId();
        c.Raise(new ToolCallStarted(callId, ToolKind.Other, name, "", json));
        var values = JsonSerializer.Deserialize<Dictionary<string, object?>>(json, StructuredOutput.SerializerOptions) ?? [];
        var result = await tool.Function.InvokeAsync(new AIFunctionArguments(values), ct);
        c.Raise(new ToolCallCompleted(callId, true, result?.ToString(), null));
    });

    public ScriptedTurn Say(string text) => Step((c, _) =>
    {
        c.Raise(new AssistantMessage(text));
        return Task.CompletedTask;
    });

    public ScriptedTurn Usage(long inputTokens, long outputTokens, string? model = null) => Step((c, _) =>
    {
        c.Raise(new ModelUsage(model ?? c.Model, inputTokens, outputTokens));
        return Task.CompletedTask;
    });

    public ScriptedTurn Wait(TimeSpan delay) => Step((_, ct) => Task.Delay(delay, ct));

    public ScriptedTurn Reply(string text) => Add((c, _) =>
    {
        c.Raise(new AssistantMessage(text));
        return Task.FromResult<string?>(text);
    });

    public ScriptedTurn ReplyJson(object value) => Reply(JsonSerializer.Serialize(value, StructuredOutput.SerializerOptions));

    public ScriptedTurn Step(Func<ScriptedBackend.ScriptedContext, CancellationToken, Task> step) => Add(async (c, ct) =>
    {
        await step(c, ct);
        return null;
    });

    private ScriptedTurn Add(Func<ScriptedBackend.ScriptedContext, CancellationToken, Task<string?>> step)
    {
        _steps.Add(step);
        return this;
    }
}
