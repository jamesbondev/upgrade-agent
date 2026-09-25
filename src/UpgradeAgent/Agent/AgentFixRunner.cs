using UpgradeAgent.Agent.Activities;
using UpgradeAgent.Build;
using UpgradeAgent.Config;
using UpgradeAgent.Infrastructure;
using UpgradeAgent.Run;
using UpgradeAgent.Ui;

namespace UpgradeAgent.Agent;

/// <summary>
/// Fixes a broken group with an AI agent. This is the provider-neutral half: it grounds the model (migration
/// notes, current errors), decides what the agent may do, enforces the budgets and stop rules, and asks for a
/// structured summary. <see cref="IAgentBackend"/> is the provider half. The outcome is never trusted: the
/// orchestrator rebuilds, retests and runs the guardrails afterwards.
/// </summary>
internal sealed class AgentFixRunner(
    IAgentBackend backend,
    AgentOptions options,
    PackageDocsLocator docsLocator,
    AgentActivity activity,
    IApprovalPrompter prompter,
    TimeProvider time) : IGroupFixer
{
    private static readonly TimeSpan SummaryTimeout = TimeSpan.FromMinutes(2);

    public async Task<FixOutcome> FixAsync(FixContext context, CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        var worktree = context.WorktreePath;
        var globalPackages = await docsLocator.GlobalPackagesFolderAsync(worktree, cancellationToken);
        var docs = context.Group.Updates.Select(u => PackageDocsLocator.Find(globalPackages, u.Id, u.To.ToNormalizedString())).ToList();
        var task = FixPrompts.TaskPrompt(context.Group, docs, context.Build, context.Tests, worktree, File.ReadAllText);
        var systemPrompt = FixPrompts.SystemPrompt(
            RepoPath.Relative(worktree, context.SolutionPath), TestRunnerDetector.Detect(worktree), context.Group.Kind, context.TestArgs);

        using var budget = new PausableTimeout(TimeSpan.FromMinutes(options.MaxMinutesPerGroup), time);
        using var session = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
        var meter = new AgentSessionMeter(options, context.Build.Succeeded ? 0 : context.Build.Errors.Count, session.Cancel);
        var reading = new RequiredReading(task.RequiredReads);
        var policy = new CommandPolicy(worktree, globalPackages is null ? [] : [globalPackages]);
        var gate = new PermissionGate(policy, reading, prompter, activity, meter, options.AllowWebFetch, budget.Pause);
        var monitor = new SessionMonitor(worktree, activity, meter, reading, options.Model);

        using var log = new FileActivitySink(Path.Combine(context.OutputDirectory, "agent", $"{RepoPath.SafeFileName(context.Group.Name)}.log"), time);
        using var attached = activity.Attach(log);
        using var span = AgentTelemetry.StartSession(backend.Name, context.Group.Name, options.Model);
        activity.Write(new Note(
            $"agent: {backend.Name}{(options.Model is null ? "" : $" ({options.Model})")} · budget {options.MaxMinutesPerGroup} min / {options.MaxToolCallsPerGroup} tool calls"));
        activity.Write(new Transcript("TASK", task.Text));

        var sessionStarted = false;
        GroupSummary? summary = null;
        string? failure = null;
        try
        {
            var settings = new AgentSessionSettings(worktree, systemPrompt, options.AllowWebFetch, gate.AuthorizeAsync, monitor.OnEvent);
            await using var agent = await backend.StartSessionAsync(settings, session.Token);
            sessionStarted = true;
            var reply = await agent.SendAsync(task.Text, session.Token);
            activity.Write(new Transcript("FINAL", reply ?? ""));

            meter.SummaryMode = true;
            summary = await RequestSummaryAsync(agent, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // A stop rule or the time budget ended the session; the first reason recorded wins.
            meter.Stop("agent stopped: time budget exceeded");
            activity.Write(new Note(meter.StopReason!));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Model calls fail routinely (not logged in, rate limits, a crashed runtime). That rejects this
            // group like any other failure; it must not abort the run.
            failure = $"agent failed: {ex.GetBaseException().Message.Truncate(200)}";
            activity.Write(new Note(failure));
        }

        var stats = meter.Snapshot(time.GetElapsedTime(started));
        span?.SetTag("gen_ai.response.model", stats.Model)
            .SetTag("gen_ai.usage.input_tokens", stats.InputTokens)
            .SetTag("gen_ai.usage.output_tokens", stats.OutputTokens)
            .SetTag("upgrade_agent.stop_reason", meter.StopReason);

        var headline = failure
            ?? (meter.StopReason is { } reason
                ? $"{reason} after {Describe(stats)}"
                : $"finished in {Describe(stats)}{(summary is null ? "; no structured summary" : "")}");
        return new FixOutcome(sessionStarted, headline, summary, stats);
    }

    public ValueTask DisposeAsync() => backend.DisposeAsync();

    /// <summary>
    /// A second turn on the same session, with every tool refused. It has its own timeout, and a missing or
    /// malformed summary is only a note: the fix itself already finished.
    /// </summary>
    private async Task<GroupSummary?> RequestSummaryAsync(IAgentSession agent, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(SummaryTimeout, time);
        using var turn = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var reply = await agent.SendAsync(FixPrompts.SummaryRequest(), turn.Token);
            activity.Write(new Transcript("SUMMARY", reply ?? ""));
            return GroupSummaryParser.TryParse(reply);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            activity.Write(new Note($"no structured summary: {(ex is OperationCanceledException ? "the agent didn't reply in time" : ex.GetBaseException().Message.Truncate(120))}"));
            return null;
        }
    }

    private static string Describe(AgentStats stats) =>
        $"{stats.Duration.TotalMinutes:0.0} min · {stats.ModelCalls} model calls · {stats.ToolCalls} tool calls · {stats.InputTokens / 1000}k in / {stats.OutputTokens / 1000}k out tokens";
}
