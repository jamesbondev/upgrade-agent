using System.Collections.Concurrent;
using System.Diagnostics;
using AgentHarness.Policies;

namespace AgentHarness;

/// <summary>
/// A conversation with an agent, with the harness's rules around it: every tool action goes through the policy
/// (and the operator, when the policy asks), the limits and stop rules end a session that runs away, and every
/// event reaches the observers. Thread-safe: backends raise events and permission requests on their own threads.
/// </summary>
public sealed class AgentSession : IAsyncDisposable
{
    private const string DefaultDeclinedFeedback = "The operator declined this. Find another way that stays within the rules.";

    private static readonly TimeSpan DefaultAskTimeout = TimeSpan.FromMinutes(2);

    private readonly AgentSessionOptions _options;
    private readonly IAgentBackend _backend;
    private readonly IApprovalPrompter _prompter;
    private readonly TimeProvider _time;
    private readonly long _started;
    private readonly PausableTimeout _budget;
    private readonly CancellationTokenSource _stop = new();
    private readonly CancellationTokenSource _lifetime;
    private readonly Activity? _span;
    private readonly Lock _events = new();
    private readonly Lock _state = new();
    private readonly ConcurrentDictionary<string, ToolCallStarted> _running = new(StringComparer.Ordinal);
    private IAgentBackendSession? _session;
    private IDisposable _idle;
    private volatile bool _toolsOff;
    private int _modelCalls;
    private int _toolCalls;
    private long _inputTokens;
    private long _outputTokens;
    private double _aiCredits;
    private int _operatorApprovals;
    private int _refusals;
    private int _countedRefusals;
    private string? _servedModel;
    private string? _stopReason;
    private int _disposed;

    private AgentSession(IAgentBackend backend, AgentSessionOptions options, IApprovalPrompter prompter, TimeProvider time)
    {
        _backend = backend;
        _options = options;
        _prompter = prompter;
        _time = time;
        _started = time.GetTimestamp();
        _budget = new PausableTimeout(options.Limits.MaxDuration is { } d && d > TimeSpan.Zero ? d : null, time);

        // The budget runs only while the agent is working (starting up, or in a turn), not between turns.
        _idle = _budget.Pause();
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(_budget.Token, _stop.Token);
        _span = AgentTelemetry.StartSession(backend.Name, options.Name, backend.Model);
    }

    public string Name => _options.Name;

    /// <summary>Why the session was stopped, or null while it may continue.</summary>
    public string? StopReason
    {
        get
        {
            lock (_state)
            {
                return _stopReason;
            }
        }
    }

    public AgentStats Stats
    {
        get
        {
            lock (_state)
            {
                return new AgentStats(
                    _servedModel, _modelCalls, _toolCalls, _inputTokens, _outputTokens, _aiCredits, _operatorApprovals, _refusals, _stopReason,
                    _time.GetElapsedTime(_started));
            }
        }
    }

    internal static async Task<AgentSession> StartAsync(
        IAgentBackend backend, AgentSessionOptions options, IApprovalPrompter prompter, TimeProvider time, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WorkingDirectory);
        ArgumentNullException.ThrowIfNull(options.Policy);

        var session = new AgentSession(backend, options, prompter, time);
        try
        {
            using var start = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session._lifetime.Token);
            var settings = new AgentBackendSettings(
                options.WorkingDirectory, options.Instructions, options.Tools, options.UseBuiltInTools, options.AllowWebFetch,
                (request, _) => session.AuthorizeAsync(request, session._lifetime.Token),
                session.Publish);
            using (session.Working())
            {
                session._session = await backend.StartSessionAsync(settings, start.Token);
            }

            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Sends a message and waits for the agent to finish its turn. A limit or stop rule that fires during the turn
    /// ends it early: the reply then has <see cref="AgentReply.StopReason"/> set, and the session takes no more turns.
    /// Provider failures (not signed in, rate limits, a crashed runtime) throw.
    /// </summary>
    public async Task<AgentReply> SendAsync(string message, CancellationToken cancellationToken = default)
    {
        var session = _session ?? throw new ObjectDisposedException(nameof(AgentSession));
        if (StopReason is { } stopped)
        {
            return new AgentReply(null, stopped);
        }

        using var turn = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        using var working = Working();
        Publish(new UserMessage(message));
        try
        {
            var text = await session.SendAsync(message, turn.Token);
            return new AgentReply(text, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && _lifetime.IsCancellationRequested)
        {
            // A stop rule or the time budget ended the turn; the first reason recorded wins.
            StopForLimit("agent stopped: time budget exceeded");
            return new AgentReply(null, StopReason);
        }
    }

    /// <summary>
    /// Asks for a structured reply, with every tool refused. The JSON schema comes from <typeparamref name="T"/>.
    /// Has its own timeout (default 2 minutes) outside the session's budget, and works after a normal turn; a
    /// reply that isn't valid JSON for <typeparamref name="T"/> is returned with an error, not thrown; so is a provider
    /// failure (then <see cref="StructuredReply{T}.Text"/> is null and the error names the exception).
    /// </summary>
    public Task<StructuredReply<T>> AskAsync<T>(string question, CancellationToken cancellationToken)
        where T : class => AskAsync<T>(question, validate: null, timeout: null, cancellationToken);

    /// <inheritdoc cref="AskAsync{T}(string, CancellationToken)"/>
    /// <param name="validate">Checks JSON can't express (non-empty strings, ranges): returns an error, or null when the value is fine.</param>
    public async Task<StructuredReply<T>> AskAsync<T>(
        string question, Func<T, string?>? validate = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
        where T : class
    {
        try
        {
            var reply = StructuredOutput.Parse<T>(await SendWithoutToolsAsync(StructuredOutput.PromptFor<T>(question), timeout, cancellationToken));
            return reply.Value is { } value && validate?.Invoke(value) is { } error ? reply with { Value = null, Error = error } : reply;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new StructuredReply<T>(null, null, ex is OperationCanceledException ? "the agent didn't reply in time" : ex.GetBaseException().Message);
        }
    }

    /// <summary>
    /// A turn in which every tool is refused and no limit or stop rule applies: for summaries and follow-up
    /// questions after the work is done. Throws on timeout (default 2 minutes) and on provider failures.
    /// </summary>
    public Task<string?> SendWithoutToolsAsync(string message, CancellationToken cancellationToken) => SendWithoutToolsAsync(message, null, cancellationToken);

    /// <inheritdoc cref="SendWithoutToolsAsync(string, CancellationToken)"/>
    public async Task<string?> SendWithoutToolsAsync(string message, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var session = _session ?? throw new ObjectDisposedException(nameof(AgentSession));
        using var limit = new CancellationTokenSource(timeout ?? DefaultAskTimeout, _time);
        using var turn = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, limit.Token);
        _toolsOff = true;
        try
        {
            Publish(new UserMessage(message) { WithoutTools = true });
            return await session.SendAsync(message, turn.Token);
        }
        finally
        {
            _toolsOff = false;
        }
    }

    /// <summary>
    /// Stops the session for a reason of your own, unless it has already stopped. A running <see cref="SendAsync"/>
    /// turn returns early; a running tools-off turn finishes, and later turns return stopped.
    /// </summary>
    public void Stop(string reason) => Stop(reason, fromLimit: false);

    /// <summary>Limits and stop rules don't apply to tools-off turns: late events must not stop a finished session.</summary>
    private void StopForLimit(string reason) => Stop(reason, fromLimit: true);

    private void Stop(string reason, bool fromLimit)
    {
        lock (_state)
        {
            if (_stopReason is not null || (fromLimit && _toolsOff))
            {
                return;
            }

            _stopReason = reason;
        }

        Publish(new SessionStopped(reason));
        try
        {
            _stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Stop rules can fire from SDK callbacks that arrive after the session was torn down.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_session is { } session)
        {
            _session = null;
            await session.DisposeAsync();
        }

        var stats = Stats;
        _span?.SetTag("gen_ai.response.model", stats.Model)
            .SetTag("gen_ai.usage.input_tokens", stats.InputTokens)
            .SetTag("gen_ai.usage.output_tokens", stats.OutputTokens)
            .SetTag("agent_harness.stop_reason", stats.StopReason)
            .Dispose();
        _lifetime.Dispose();
        _stop.Dispose();
        _budget.Dispose();
    }

    /// <summary>Runs the budget until disposed.</summary>
    private WorkingScope Working()
    {
        _idle.Dispose();
        return new WorkingScope(this);
    }

    private sealed class WorkingScope(AgentSession owner) : IDisposable
    {
        public void Dispose() => owner._idle = owner._budget.Pause();
    }

    private async Task<ToolApproval> AuthorizeAsync(ToolRequest request, CancellationToken cancellationToken)
    {
        if (_toolsOff)
        {
            return ToolApproval.Deny("No tools now: reply with the answer only.");
        }

        var action = PathText.Relative(_options.WorkingDirectory, request.Describe());
        if (request is CustomToolRequest custom)
        {
            var tool = _options.Tools.FirstOrDefault(t => t.Name == custom.Name);
            if (tool is null)
            {
                return Refuse(request, action, ToolDecision.Reject($"'{custom.Name}' is not available in this session."));
            }

            if (!tool.RequiresApproval)
            {
                return ToolApproval.Allow;
            }
        }

        if (request is WebFetchRequest && !_options.AllowWebFetch)
        {
            return Refuse(request, action, ToolDecision.Reject("Web access is disabled in this session."));
        }

        var decision = await _options.Policy.EvaluateAsync(request, cancellationToken);
        switch (decision.Verdict)
        {
            case ToolVerdict.Approve:
                return ToolApproval.Allow;

            case ToolVerdict.Ask:
                bool approved;
                using (_budget.Pause())
                {
                    approved = await _prompter.ConfirmAsync(decision.Prompt ?? action, decision.Reason, cancellationToken);
                }

                if (!approved)
                {
                    return Refuse(request, action, ToolDecision.Reject(decision.DeclinedFeedback ?? DefaultDeclinedFeedback) with { LogReason = "declined (needs operator approval)" }, byOperator: true);
                }

                lock (_state)
                {
                    _operatorApprovals++;
                }

                Publish(new ToolApprovedByOperator(request, action));
                return ToolApproval.Allow;

            default:
                return Refuse(request, action, decision);
        }
    }

    private ToolApproval Refuse(ToolRequest request, string action, ToolDecision decision, bool byOperator = false)
    {
        string? stop = null;
        lock (_state)
        {
            _refusals++;
            if (decision.CountsTowardRefusalLimit && ++_countedRefusals > _options.Limits.MaxRefusals && _options.Limits.MaxRefusals > 0)
            {
                stop = $"agent stopped: more than {_options.Limits.MaxRefusals} refused actions";
            }
        }

        Publish(new ToolRefused(request, action, decision.LogReason ?? decision.Reason, decision.CountsTowardRefusalLimit) { DeclinedByOperator = byOperator });
        if (stop is not null)
        {
            StopForLimit(stop);
        }

        return ToolApproval.Deny(decision.Reason);
    }

    /// <summary>Counts the event, pairs tool calls, and hands it to the observers and stop rules, one event at a time.</summary>
    private void Publish(AgentEvent agentEvent)
    {
        var (tracked, follow, stop) = Track(agentEvent);
        lock (_events)
        {
            foreach (var observer in _options.Observers)
            {
                observer.OnEvent(tracked);
            }

            if (!_toolsOff)
            {
                foreach (var rule in _options.StopRules)
                {
                    stop ??= rule.Check(tracked);
                }
            }
        }

        if (follow is not null)
        {
            Publish(follow);
        }

        if (stop is not null)
        {
            StopForLimit(stop);
        }
    }

    private (AgentEvent Tracked, AgentEvent? Follow, string? Stop) Track(AgentEvent agentEvent)
    {
        switch (agentEvent)
        {
            case ToolCallStarted started:
                _running[started.CallId] = started;
                AgentTelemetry.RecordToolCall(started.Tool);
                lock (_state)
                {
                    var limit = _options.Limits.MaxToolCalls;
                    return (started, null, ++_toolCalls > limit && limit > 0 ? $"agent stopped: more than {limit} tool calls" : null);
                }

            case ToolCallCompleted completed:
                return (_running.TryRemove(completed.CallId, out var call) ? completed with { Call = call } : completed, null, null);

            case ModelUsage usage:
                AgentTelemetry.RecordUsage(usage);
                lock (_state)
                {
                    _modelCalls++;
                    _inputTokens += usage.InputTokens;
                    _outputTokens += usage.OutputTokens;
                    _aiCredits += usage.AiCredits;
                    if (usage.Model is null || usage.Model == _servedModel)
                    {
                        return (usage, null, null);
                    }

                    _servedModel = usage.Model;
                    return (usage, new ModelServed(usage.Model, _backend.Model), null);
                }

            default:
                return (agentEvent, null, null);
        }
    }
}
