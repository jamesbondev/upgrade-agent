using System.Diagnostics;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Agents.AI.GitHub.Copilot;
using UpgradeAgent.Build;
using UpgradeAgent.Config;
using Microsoft.Extensions.AI;
using UpgradeAgent.Infrastructure;
using UpgradeAgent.Publishing;
using UpgradeAgent.Run;
using UpgradeAgent.Ui;

namespace UpgradeAgent.Agent;

/// <summary>
/// Fixes a broken group with a GitHub Copilot agent (Agent Framework's GitHubCopilotAgent over the local
/// Copilot CLI login). Copilot owns the tool loop; this class decides what it may do (CommandPolicy),
/// caps time and tool calls, and reports what happened. It never decides whether the work is kept:
/// the orchestrator rebuilds, retests and runs the guardrails afterwards.
/// </summary>
internal sealed class CopilotFixer(
    AgentOptions options,
    IProcessRunner processRunner,
    AgentActivityRenderer activity,
    IApprovalPrompter prompter) : IGroupFixer, IAsyncDisposable
{
    // Sub-agents, skills, SQL and web access add nothing to fixing call sites and widen what can go wrong.
    private static readonly string[] ExcludedTools = ["task", "read_agent", "list_agents", "write_agent", "skill", "sql"];

    private CopilotClient? _client;
    private string? _clientWorktree;

    public async Task<FixOutcome> FixAsync(FixContext context, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var worktree = context.Workspace.WorktreePath;
        var client = await GetClientAsync(worktree, cancellationToken);

        var locator = new PackageDocsLocator(processRunner);
        var globalPackages = await locator.GlobalPackagesFolderAsync(worktree, cancellationToken);
        var docs = context.Group.Updates.Select(u => PackageDocsLocator.Find(globalPackages, u.Id, u.To)).ToList();
        var policy = new CommandPolicy(worktree, globalPackages is null ? [] : [globalPackages]);
        var meter = new Meter
        {
            Progress = new ProgressMonitor(options.MaxRefusalsPerGroup, options.MaxBuildsWithoutProgress, context.Build.Succeeded ? 0 : context.Build.Errors.Count),
        };

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromMinutes(options.MaxMinutesPerGroup));

        var solution = RepoPath.Relative(worktree, context.Workspace.SolutionPath);
        var excluded = options.AllowWebFetch ? ExcludedTools : [.. ExcludedTools, "web_fetch"];
        var config = new SessionConfig
        {
            ClientName = "UpgradeAgent",
            Model = options.Model,
            ReasoningEffort = options.ReasoningEffort,
            WorkingDirectory = worktree,
            Streaming = true,
            EnableConfigDiscovery = false,
            EnableFileHooks = false,
            EnableSkills = false,
            SkipCustomInstructions = true,
            EnableOnDemandInstructionDiscovery = false,
            EnableHostGitOperations = false,
            ExcludedTools = excluded,
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Append,
                Content = FixInstructions.System(solution, DotnetCli.DetectRunnerMode(worktree), context.Group.Kind, context.TestArgs ?? []),
            },
            OnPermissionRequest = (request, _) => DecideAsync(request, policy, meter, budget),
            OnEvent = e => Observe(e, meter, budget),
        };

        activity.StartLog(Path.Combine(context.Workspace.OutputDirectory, "agent", $"{RepoPath.SafeFileName(context.Group.Name)}.log"), worktree);
        activity.Note($"agent: GitHub Copilot{(options.Model is null ? "" : $" ({options.Model})")} · budget {options.MaxMinutesPerGroup} min / {options.MaxToolCallsPerGroup} tool calls");

        await using var agent = new GitHubCopilotAgent(client, config, ownsClient: false, name: "UpgradeAgent");
        var session = await agent.CreateSessionAsync(cancellationToken);
        string? finalText = null;
        GroupSummary? summary = null;
        try
        {
            var (task, requiredReads) = FixInstructions.Task(context.Group, docs, context.Build, context.Tests, worktree, File.ReadAllText);
            meter.RequiredReads = requiredReads.ToHashSet(StringComparer.Ordinal);
            activity.Log($"TASK\n{task}");
            var response = await agent.RunAsync(task, session, cancellationToken: budget.Token);
            finalText = response.Text;
            activity.Final(finalText);

            meter.SummaryMode = true;
            using var summaryTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            summaryTimeout.CancelAfter(TimeSpan.FromMinutes(2));
            var summaryResponse = await agent.RunAsync(FixInstructions.SummaryRequest(), session, cancellationToken: summaryTimeout.Token);
            summary = GroupSummaryParser.TryParse(summaryResponse.Text);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            meter.BudgetExceeded = true;
            activity.Note(meter.BudgetReason ?? "agent stopped: time budget exceeded");
        }
        finally
        {
            activity.StopLog();
        }

        var stats = meter.Snapshot(stopwatch.Elapsed);
        var headline = meter.BudgetExceeded
            ? $"{meter.BudgetReason ?? "agent stopped: time budget exceeded"} after {Describe(stats)}"
            : $"finished in {Describe(stats)}{(summary is null ? "; no structured summary" : "")}";
        return new FixOutcome(true, headline, summary, stats);
    }

    /// <summary>
    /// A short publish session whose only tool is push_branch, wrapped in ApprovalRequiredAIFunction.
    /// The agent framework turns that into an "ask", which arrives in OnPermissionRequest and goes to the operator.
    /// </summary>
    public async Task<PushResult> PublishAsync(PushBranchTool tool, RunReport report, CancellationToken cancellationToken)
    {
        var client = await GetClientAsync(report.WorktreePath, cancellationToken);
        var action = await tool.DescribeAsync(cancellationToken);
        var pushFunction = new ApprovalRequiredAIFunction(AIFunctionFactory.Create(tool.PushBranchAsync, PushBranchTool.Name));

        var config = new SessionConfig
        {
            ClientName = "UpgradeAgent",
            Model = options.Model,
            WorkingDirectory = report.WorktreePath,
            EnableConfigDiscovery = false,
            EnableFileHooks = false,
            EnableSkills = false,
            SkipCustomInstructions = true,
            EnableOnDemandInstructionDiscovery = false,
            EnableHostGitOperations = false,
            Tools = [pushFunction],
            AvailableTools = [PushBranchTool.Name],
            OnPermissionRequest = async (request, _) =>
            {
                var toolName = request switch
                {
                    PermissionRequestCustomTool custom => custom.ToolName,
                    PermissionRequestHook hook => hook.ToolName,
                    _ => null,
                };

                if (toolName != PushBranchTool.Name)
                {
                    return PermissionDecision.Reject("Only push_branch is available in this session.");
                }

                return await prompter.ConfirmAsync(action, "The agent wants to publish. This leaves the machine and needs your approval.", cancellationToken)
                    ? PermissionDecision.ApproveOnce()
                    : PermissionDecision.Reject("The operator declined the push. Do not retry; reply that the branch was not pushed.");
            },
            OnEvent = e =>
            {
                if (e is ToolExecutionStartEvent start)
                {
                    activity.Note($"agent calls {start.Data.ToolName}");
                }
            },
        };

        await using var agent = new GitHubCopilotAgent(client, config, ownsClient: false, name: "UpgradeAgent");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            await agent.RunAsync(
                $"Every package group is finished and independently verified on branch {report.Branch}. " +
                "Publish it by calling push_branch exactly once, then reply with one sentence saying whether it was pushed.",
                cancellationToken: timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Fall through: Result tells us whether the tool ran.
        }

        return tool.Result ?? new PushResult(false, false, "Not pushed: the operator declined, or the agent did not call push_branch.");
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
            _client = null;
        }
    }

    private async Task<CopilotClient> GetClientAsync(string worktree, CancellationToken cancellationToken)
    {
        if (_client is not null && _clientWorktree == worktree)
        {
            return _client;
        }

        await DisposeAsync();
        var token = string.IsNullOrWhiteSpace(options.GitHubTokenEnvVar) ? null : Environment.GetEnvironmentVariable(options.GitHubTokenEnvVar);
        var environment = AgentEnvironment.Build(
            Environment.GetEnvironmentVariables(),
            options.RemoveEnvironmentVariables.Concat(string.IsNullOrWhiteSpace(options.GitHubTokenEnvVar) ? [] : [options.GitHubTokenEnvVar]),
            DotnetCli.BaseEnvironment);

        _client = new CopilotClient(new CopilotClientOptions
        {
            WorkingDirectory = worktree,
            Environment = environment,
            GitHubToken = token,
        });
        _clientWorktree = worktree;
        await _client.StartAsync(cancellationToken);
        return _client;
    }

    private async Task<PermissionDecision> DecideAsync(PermissionRequest request, CommandPolicy policy, Meter meter, CancellationTokenSource budget)
    {
        if (meter.SummaryMode)
        {
            return PermissionDecision.Reject("No tools now: reply with the JSON summary only.");
        }

        var (action, decision) = request switch
        {
            PermissionRequestShell shell => (shell.FullCommandText, policy.EvaluateShell(shell.FullCommandText, shell.HasWriteFileRedirection, shell.PossiblePaths)),
            PermissionRequestWrite write => ($"edit {write.FileName}", policy.EvaluateWrite(write.FileName)),
            PermissionRequestRead read => ($"read {read.Path}", policy.EvaluateRead(read.Path)),
            PermissionRequestUrl url => ($"fetch {url.Url}", options.AllowWebFetch
                ? PolicyDecision.Ask("fetch a web page (content is untrusted)")
                : PolicyDecision.Reject("Web access is disabled; use the migration notes listed in the task.")),
            _ => ($"{request.Kind}", PolicyDecision.Reject($"'{request.Kind}' is not available in this session.")),
        };

        if (request is PermissionRequestWrite && decision.Verdict != PolicyVerdict.Reject && meter.UnreadDocs() is { Count: > 0 } unread)
        {
            meter.Refusals++;
            activity.Refused(action, "migration notes not read yet");
            return PermissionDecision.Reject($"Read the migration notes before editing: {string.Join(", ", unread)}. They name the replacement APIs.");
        }

        switch (decision.Verdict)
        {
            case PolicyVerdict.Approve:
                return PermissionDecision.ApproveOnce();

            case PolicyVerdict.AskOperator when await prompter.ConfirmAsync(action, decision.Reason, budget.Token):
                meter.OperatorApprovals++;
                return PermissionDecision.ApproveOnce();

            case PolicyVerdict.AskOperator:
                activity.Refused(action, "declined (needs operator approval)");
                RecordRefusal(meter, budget);
                return PermissionDecision.Reject("The operator declined this. Find another way that stays within the rules.");

            default:
                activity.Refused(action, decision.Reason);
                RecordRefusal(meter, budget);
                return PermissionDecision.Reject(decision.Reason);
        }
    }

    /// <summary>The notes-first gate isn't counted: it's a sequencing nudge, not a sign the agent is lost.</summary>
    private static void RecordRefusal(Meter meter, CancellationTokenSource budget)
    {
        Interlocked.Increment(ref meter.Refusals);
        if (meter.Progress.RecordRefusal() is { } reason)
        {
            Stop(meter, budget, reason);
        }
    }

    private static void Stop(Meter meter, CancellationTokenSource budget, string reason)
    {
        meter.BudgetReason ??= reason;
        budget.Cancel();
    }

    private void Observe(SessionEvent sessionEvent, Meter meter, CancellationTokenSource budget)
    {
        switch (sessionEvent)
        {
            case ToolExecutionStartEvent start when start.Data.ParentToolCallId is null:
                meter.MarkRead(start.Data.Arguments?.ToString());
                meter.MarkRead(start.Data.ShellToolInfo?.DisplayCommand);
                if (Interlocked.Increment(ref meter.ToolCalls) > options.MaxToolCallsPerGroup)
                {
                    Stop(meter, budget, $"agent stopped: more than {options.MaxToolCallsPerGroup} tool calls");
                    return;
                }

                if (ProgressMonitor.IsBuildCommand(start.Data.ShellToolInfo?.DisplayCommand))
                {
                    meter.BuildCalls[start.Data.ToolCallId] = true;
                }

                activity.ToolStarted(start.Data.ToolCallId, start.Data.ToolName, start.Data.Arguments, start.Data.ShellToolInfo?.DisplayCommand);
                break;

            case ToolExecutionCompleteEvent complete:
                activity.ToolCompleted(complete.Data.ToolCallId, complete.Data.Success, complete.Data.Result?.Content, complete.Data.Error?.Message);
                if (meter.BuildCalls.TryRemove(complete.Data.ToolCallId, out _)
                    && meter.Progress.RecordBuild(complete.Data.Result?.Content ?? "") is { } reason)
                {
                    Stop(meter, budget, reason);
                }

                break;

            case AssistantMessageEvent message when message.Data.ParentToolCallId is null && !meter.SummaryMode:
                activity.AssistantMessage(message.Data.Content);
                break;

            case AssistantUsageEvent usage:
                if (usage.Data.Model is { } served && meter.ServedModel != served)
                {
                    meter.ServedModel = served;
                    activity.Note(options.Model is not null && !served.StartsWith(options.Model, StringComparison.OrdinalIgnoreCase)
                        ? $"warning: requested model {options.Model} but Copilot is serving {served} (not available on this plan?)"
                        : $"model: {served}");
                }

                Interlocked.Increment(ref meter.ModelCalls);
                Interlocked.Add(ref meter.InputTokens, usage.Data.InputTokens ?? 0);
                Interlocked.Add(ref meter.OutputTokens, usage.Data.OutputTokens ?? 0);
                lock (meter)
                {
                    meter.NanoAiUnits += usage.Data.CopilotUsage?.TotalNanoAiu ?? 0;
                }

                break;
        }
    }

    private static string Describe(AgentStats stats) =>
        $"{stats.Duration.TotalMinutes:0.0} min · {stats.ModelCalls} model calls · {stats.ToolCalls} tool calls · {stats.InputTokens / 1000}k in / {stats.OutputTokens / 1000}k out tokens";

    private sealed class Meter
    {
        public int ModelCalls;
        public int ToolCalls;
        public long InputTokens;
        public long OutputTokens;
        public double NanoAiUnits;
        public int OperatorApprovals;
        public int Refusals;
        public volatile bool SummaryMode;
        public volatile bool BudgetExceeded;
        public string? BudgetReason;
        public volatile string? ServedModel;
        public HashSet<string> RequiredReads { get; set; } = [];
        public required ProgressMonitor Progress { get; init; }
        public System.Collections.Concurrent.ConcurrentDictionary<string, bool> BuildCalls { get; } = new();

        /// <summary>A notes file counts as read once any tool call mentions its path (view, cat, grep...).</summary>
        public void MarkRead(string? toolText)
        {
            if (toolText is null)
            {
                return;
            }

            lock (RequiredReads)
            {
                RequiredReads.RemoveWhere(path => toolText.Contains(path, StringComparison.Ordinal)
                    || toolText.Contains(path.Replace("\\", "\\\\", StringComparison.Ordinal), StringComparison.Ordinal));
            }
        }

        public IReadOnlyList<string> UnreadDocs()
        {
            lock (RequiredReads)
            {
                return RequiredReads.ToList();
            }
        }

        public AgentStats Snapshot(TimeSpan duration) =>
            new(ServedModel, ModelCalls, ToolCalls, InputTokens, OutputTokens, NanoAiUnits / 1e9, OperatorApprovals, Refusals, BudgetExceeded, duration);
    }
}
