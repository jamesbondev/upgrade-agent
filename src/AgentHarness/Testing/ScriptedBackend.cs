using System.Text.Json;
using Microsoft.Extensions.AI;

namespace AgentHarness.Testing;

/// <summary>What the session answered when the script asked to run something.</summary>
public sealed record ScriptedDecision(ToolRequest Request, bool Allowed, string? Feedback);

/// <summary>
/// A fake model for tests and demos: each turn plays back a script of tool calls and a reply, through the same
/// permission pipeline, limits, observers and stop rules as a real session. No network, no model, deterministic.
/// <code>
/// var backend = new ScriptedBackend()
///     .Turn(t => t.Shell("dotnet build --no-restore", output: "Build succeeded.").Edit("src/Foo.cs").Reply("Fixed."))
///     .Turn(t => t.ReplyJson(new Summary { Files = ["src/Foo.cs"] }));
/// var runner = new AgentRunner(backend);
/// // … then assert on backend.Decisions: what your policy allowed and refused.
/// </code>
/// Shell commands are not run; they return their scripted output. Edits with content are written, so tests
/// can check effects. Calls to your <see cref="AgentTool"/>s run the real function.
/// </summary>
public sealed class ScriptedBackend(string? model = "scripted-model") : IAgentBackend
{
    private readonly Queue<ScriptedTurn> _turns = new();
    private readonly List<ScriptedDecision> _decisions = [];
    private readonly List<string> _messages = [];

    public string Name => "Scripted";

    public string? Model => model;

    /// <summary>Every permission request the script made, and the answer, in order.</summary>
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

    /// <summary>Every message the app sent, in order (including structured-reply prompts).</summary>
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

    /// <summary>What the last session was started with: instructions, tools, flags.</summary>
    public AgentBackendSettings? LastSettings { get; private set; }

    /// <summary>Adds the next turn's script.</summary>
    public ScriptedBackend Turn(Action<ScriptedTurn> script)
    {
        var turn = new ScriptedTurn();
        script(turn);
        _turns.Enqueue(turn);
        return this;
    }

    /// <summary>A turn that only replies.</summary>
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

    /// <summary>What a custom step can use: the session's settings, and the permission pipeline.</summary>
    public sealed class ScriptedContext
    {
        private readonly Session _session;

        internal ScriptedContext(object session, AgentBackendSettings settings)
        {
            _session = (Session)session;
            Settings = settings;
        }

        public AgentBackendSettings Settings { get; }

        public string NextCallId() => _session.NextCallId();

        /// <summary>Asks the session, as the real runtime would before a tool runs. Recorded in <see cref="Decisions"/>.</summary>
        public Task<bool> AuthorizeAsync(ToolRequest request, CancellationToken cancellationToken) => _session.AuthorizeAsync(request, cancellationToken);

        public void Raise(AgentEvent agentEvent) => Settings.OnEvent(agentEvent);

        public string FullPath(string path) => Path.GetFullPath(path, Settings.WorkingDirectory);

        /// <summary>Asks for permission and, when allowed, raises the start and completion events around <paramref name="run"/>.</summary>
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

/// <summary>One turn of a <see cref="ScriptedBackend"/>: steps run in order; the last <see cref="Reply"/> is the turn's reply.</summary>
public sealed class ScriptedTurn
{
    private readonly List<Func<ScriptedBackend.ScriptedContext, CancellationToken, Task<string?>>> _steps = [];

    internal IReadOnlyList<Func<ScriptedBackend.ScriptedContext, CancellationToken, Task<string?>>> Steps => _steps;

    /// <summary>A shell command. Not run: <paramref name="output"/> is what it "printed".</summary>
    public ScriptedTurn Shell(string command, string output = "", bool success = true, bool writesFile = false) => Step(async (c, ct) =>
        await c.RunToolAsync(new ShellRequest(command, writesFile), ToolKind.Shell, "bash", command, () => Task.FromResult((success, (string?)output)), ct));

    /// <summary>An edit. With <paramref name="content"/>, the file is really written when the edit is allowed.</summary>
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

    /// <summary>A file read. Returns the real file's content when it exists.</summary>
    public ScriptedTurn Read(string path) => Step(async (c, ct) =>
    {
        var full = c.FullPath(path);
        await c.RunToolAsync(new FileReadRequest(full), ToolKind.Read, "view", full, async () =>
            File.Exists(full) ? (true, await File.ReadAllTextAsync(full, ct)) : (false, $"{path} does not exist"), ct);
    });

    public ScriptedTurn Fetch(string url, string content = "") => Step(async (c, ct) =>
        await c.RunToolAsync(new WebFetchRequest(url), ToolKind.Other, "web_fetch", url, () => Task.FromResult((true, (string?)content)), ct));

    /// <summary>Calls one of the session's <see cref="AgentTool"/>s for real, asking first when it requires approval.</summary>
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

        if (tool.RequiresApproval && !await c.AuthorizeAsync(new CustomToolRequest(name, json), ct))
        {
            return;
        }

        var callId = c.NextCallId();
        c.Raise(new ToolCallStarted(callId, ToolKind.Other, name, "", json));
        var values = JsonSerializer.Deserialize<Dictionary<string, object?>>(json, StructuredOutput.SerializerOptions) ?? [];
        var result = await tool.Function.InvokeAsync(new AIFunctionArguments(values), ct);
        c.Raise(new ToolCallCompleted(callId, true, result?.ToString(), null));
    });

    /// <summary>Text between tool calls.</summary>
    public ScriptedTurn Say(string text) => Step((c, _) =>
    {
        c.Raise(new AssistantMessage(text));
        return Task.CompletedTask;
    });

    public ScriptedTurn Usage(long inputTokens, long outputTokens, string? model = null) => Step((c, _) =>
    {
        c.Raise(new ModelUsage(model ?? "scripted-model", inputTokens, outputTokens));
        return Task.CompletedTask;
    });

    /// <summary>Waits, honouring cancellation: for testing time budgets and stop rules.</summary>
    public ScriptedTurn Wait(TimeSpan delay) => Step((_, ct) => Task.Delay(delay, ct));

    /// <summary>The turn's reply (also raised as an <see cref="AssistantMessage"/>).</summary>
    public ScriptedTurn Reply(string text) => Add((c, _) =>
    {
        c.Raise(new AssistantMessage(text));
        return Task.FromResult<string?>(text);
    });

    /// <summary>A reply that is <paramref name="value"/> as JSON, for <see cref="AgentSession.AskAsync{T}"/>.</summary>
    public ScriptedTurn ReplyJson(object value) => Reply(JsonSerializer.Serialize(value, StructuredOutput.SerializerOptions));

    /// <summary>Anything else: raise your own events, ask for your own permissions.</summary>
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
