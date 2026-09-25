using AgentHarness;
using AgentHarness.Policies;
using UpgradeAgent.Agent.Activities;
using UpgradeAgent.Build;
using UpgradeAgent.Config;
using UpgradeAgent.Infrastructure;
using UpgradeAgent.Run;
using AgentStats = UpgradeAgent.Run.AgentStats;

namespace UpgradeAgent.Agent;

internal sealed class AgentFixRunner(
    IAgentBackend backend,
    AgentOptions options,
    PackageDocsLocator docsLocator,
    AgentActivity activity,
    IApprovalPrompter prompter,
    TimeProvider time) : IGroupFixer
{
    private readonly AgentRunner _runner = new(backend, prompter, time);

    public async Task<FixOutcome> FixAsync(FixContext context, CancellationToken cancellationToken)
    {
        var started = time.GetTimestamp();
        var worktree = context.WorktreePath;
        var globalPackages = await docsLocator.GlobalPackagesFolderAsync(worktree, cancellationToken);
        var docs = context.Group.Updates.Select(u => PackageDocsLocator.Find(globalPackages, u.Id, u.To.ToNormalizedString())).ToList();
        var task = FixPrompts.TaskPrompt(context.Group, docs, context.Build, context.Tests, worktree, File.ReadAllText);
        var systemPrompt = FixPrompts.SystemPrompt(
            RepoPath.Relative(worktree, context.SolutionPath), TestRunnerDetector.Detect(worktree), context.Group.Kind, context.TestArgs);

        var reading = new RequiredReading(task.RequiredReads);
        var monitor = new SessionMonitor(worktree, activity, reading);
        var sessionOptions = new AgentSessionOptions
        {
            Name = context.Group.Name,
            WorkingDirectory = worktree,
            Instructions = systemPrompt,
            Policy = reading.Guard(new CommandPolicy(worktree, globalPackages is null ? [] : [globalPackages])),
            AllowWebFetch = options.AllowWebFetch,
            Limits = new AgentLimits
            {
                MaxDuration = TimeSpan.FromMinutes(options.MaxMinutesPerGroup),
                MaxToolCalls = options.MaxToolCallsPerGroup,
                MaxRefusals = options.MaxRefusalsPerGroup,
            },
            Observers = [monitor],
            StopRules = [new ProgressMonitor(options.MaxBuildsWithoutProgress, context.Build.Succeeded ? 0 : context.Build.Errors.Count)],
        };

        using var log = new FileActivitySink(Path.Combine(context.OutputDirectory, "agent", $"{RepoPath.SafeFileName(context.Group.Name)}.log"), time);
        using var attached = activity.Attach(log);
        activity.Write(new Note(
            $"agent: {backend.Name}{(options.Model is null ? "" : $" ({options.Model})")} · budget {options.MaxMinutesPerGroup} min / {options.MaxToolCallsPerGroup} tool calls"));
        activity.Write(new Transcript("TASK", task.Text));

        AgentSession? session = null;
        GroupSummary? summary = null;
        string? stopReason = null;
        string? failure = null;
        try
        {
            session = await _runner.StartAsync(sessionOptions, cancellationToken);
            var reply = await session.SendAsync(task.Text, cancellationToken);
            if (!reply.Stopped)
            {
                activity.Write(new Transcript("FINAL", reply.Text ?? ""));
                monitor.SummaryMode = true;
                summary = await RequestSummaryAsync(session, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            stopReason = "agent stopped: time budget exceeded";
            activity.Write(new Note(stopReason));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failure = $"agent failed: {ex.GetBaseException().Message.Truncate(200)}";
            activity.Write(new Note(failure));
        }
        finally
        {
            if (session is not null)
            {
                await session.DisposeAsync();
            }
        }

        stopReason ??= session?.StopReason;
        var stats = ToStats(session?.Stats, stopReason is not null, time.GetElapsedTime(started));
        var headline = failure
            ?? (stopReason is { } reason
                ? $"{reason} after {Describe(stats)}"
                : $"finished in {Describe(stats)}{(summary is null ? "; no structured summary" : "")}");
        return new FixOutcome(session is not null, headline, summary, stats);
    }

    public ValueTask DisposeAsync() => backend.DisposeAsync();

    private async Task<GroupSummary?> RequestSummaryAsync(AgentSession session, CancellationToken cancellationToken)
    {
        var reply = await session.AskAsync<GroupSummary>(FixPrompts.SummaryRequest(), cancellationToken: cancellationToken);
        if (reply.Text is null)
        {
            activity.Write(new Note($"no structured summary: {(reply.Error ?? "no reply").Truncate(120)}"));
            return null;
        }

        activity.Write(new Transcript("SUMMARY", reply.Text));
        return GroupSummaryParser.Validate(reply.Value);
    }

    private static AgentStats ToStats(AgentHarness.AgentStats? stats, bool stopped, TimeSpan duration) => stats is null
        ? new AgentStats(null, 0, 0, 0, 0, 0, 0, 0, stopped, duration)
        : new AgentStats(
            stats.Model, stats.ModelCalls, stats.ToolCalls, stats.InputTokens, stats.OutputTokens, stats.AiCredits, stats.OperatorApprovals, stats.Refusals,
            stopped, duration);

    private static string Describe(AgentStats stats) =>
        $"{stats.Duration.TotalMinutes:0.0} min · {stats.ModelCalls} model calls · {stats.ToolCalls} tool calls · {stats.InputTokens / 1000}k in / {stats.OutputTokens / 1000}k out tokens";
}
