using System.CommandLine;
using System.Text.Json;
using Spectre.Console;
using UpgradeAgent;
using UpgradeAgent.Agent;
using UpgradeAgent.Config;
using UpgradeAgent.Detection;
using UpgradeAgent.Infrastructure;
using UpgradeAgent.Build;
using UpgradeAgent.Preflight;
using UpgradeAgent.Publishing;
using UpgradeAgent.Replay;
using UpgradeAgent.Run;
using UpgradeAgent.Ui;

var configOption = new Option<FileInfo?>("--config") { Description = "JSON config file layered over appsettings.json." };
var onlyOption = new Option<string[]>("--only")
{
    Description = "Limit to these package IDs (globs) or family names. Repeatable.",
    AllowMultipleArgumentsPerToken = true,
};
var planOutOption = new Option<FileInfo?>("--plan-out") { Description = "Where to write the plan JSON. Default: <Output:Directory>/plan.json." };
var planOption = new Option<FileInfo?>("--plan") { Description = "Use a saved plan instead of detecting (replays and demos)." };
var agentOption = new Option<string?>("--agent") { Description = "Agent that fixes broken groups: copilot or none. Default: Agent:Provider." };
var recordOption = new Option<string?>("--record") { Description = "Record the agent's sessions under recordings/<name> for later replay." };
var replayOption = new Option<string?>("--replay") { Description = "Replay recordings/<name> instead of calling the agent. Builds, tests and guardrails still run live." };
var replayGapOption = new Option<double>("--replay-max-gap") { Description = "Longest pause between replayed agent lines, in seconds.", DefaultValueFactory = _ => 0.8 };
var nonInteractiveOption = new Option<bool>("--non-interactive") { Description = "Never prompt; anything that needs approval is declined. Implied when output is redirected." };

var planCommand = new Command("plan", "Detect outdated packages and print the upgrade plan. Changes nothing.")
{
    configOption,
    onlyOption,
    planOutOption,
};
planCommand.SetAction((parseResult, cancellationToken) => RunPlanAsync(
    parseResult.GetValue(configOption),
    parseResult.GetValue(onlyOption) ?? [],
    parseResult.GetValue(planOutOption),
    cancellationToken));

var runCommand = new Command("run", "Upgrade packages on a new branch in a worktree beside the repo, one commit per accepted group.")
{
    configOption,
    onlyOption,
    planOption,
    agentOption,
    nonInteractiveOption,
    recordOption,
    replayOption,
    replayGapOption,
};
runCommand.SetAction((parseResult, cancellationToken) => RunUpgradeAsync(
    parseResult.GetValue(configOption),
    parseResult.GetValue(onlyOption) ?? [],
    parseResult.GetValue(planOption),
    parseResult.GetValue(agentOption),
    parseResult.GetValue(nonInteractiveOption),
    new RecordReplay(parseResult.GetValue(recordOption), parseResult.GetValue(replayOption), parseResult.GetValue(replayGapOption)),
    cancellationToken));

var root = new RootCommand("UpgradeAgent: keeps a .NET repo's NuGet packages up to date, with an AI agent fixing breaking changes.")
{
    planCommand,
    runCommand,
};

// Ctrl+C cancels the run; the current group is then reverted and the report written. The default
// 2-second grace period is too short for that, so allow 30 seconds before the process is killed.
return await root.Parse(args).InvokeAsync(new InvocationConfiguration { ProcessTerminationTimeout = TimeSpan.FromSeconds(30) });

static async Task<int> RunPlanAsync(FileInfo? configFile, string[] only, FileInfo? planOut, CancellationToken cancellationToken)
{
    var console = ConsoleFactory.Create();
    return await HandleErrorsAsync(console, async () =>
    {
        var config = ConfigLoader.Load(configFile?.FullName);
        var processRunner = new ProcessRunner();
        if (!await PreflightAsync(console, config, processRunner, requireCleanRepo: false, cancellationToken))
        {
            return ExitCodes.PreflightFailed;
        }

        console.MarkupLine("[grey]Detecting outdated packages (latest, highest minor, highest patch)...[/]");
        var reports = await new PackageListRunner(processRunner)
            .ListAllAsync(config.SolutionPath, config.Options.Policy.IncludePrerelease, cancellationToken);

        var planner = new Planner(config.Options.Policy, new NuGetPackageCompatibilityChecker(config.RepoPath));
        var plan = await planner.CreateAsync(reports, config.RepoPath, config.SolutionPath, only, cancellationToken);
        PlanRenderer.RenderPlan(console, plan, config.RepoPath);

        var planPath = planOut?.FullName ?? Path.Combine(config.OutputDirectory, "plan.json");
        Directory.CreateDirectory(Path.GetDirectoryName(planPath)!);
        await File.WriteAllTextAsync(planPath, JsonSerializer.Serialize(plan, JsonDefaults.Options), cancellationToken);
        console.MarkupLine($"[grey]Plan written to {Markup.Escape(planPath)}[/]");
        return ExitCodes.Success;
    });
}

static async Task<int> RunUpgradeAsync(
    FileInfo? configFile, string[] only, FileInfo? planFile, string? agent, bool nonInteractive, RecordReplay recordReplay, CancellationToken cancellationToken)
{
    var console = ConsoleFactory.Create();
    return await HandleErrorsAsync(console, async () =>
    {
        var config = ConfigLoader.Load(configFile?.FullName);
        var processRunner = new ProcessRunner();
        if (!await PreflightAsync(console, config, processRunner, requireCleanRepo: true, cancellationToken))
        {
            return ExitCodes.PreflightFailed;
        }

        var consoleLock = new object();
        IApprovalPrompter prompter = nonInteractive || !console.Profile.Capabilities.Interactive
            ? new DeclineAllPrompter()
            : new ConsoleApprovalPrompter(console, consoleLock);

        if (recordReplay is { Record: not null, Replay: not null })
        {
            throw new ConfigurationException("--record and --replay can't be combined.");
        }

        var git = new GitCli(processRunner);
        var recordingsRoot = Path.GetFullPath(config.Options.Output.RecordingsDirectory);
        UpgradePlan? frozenPlan = null;
        IGroupFixer fixer;
        CopilotFixer? copilot = null;
        if (recordReplay.Replay is { } replayName)
        {
            var recording = Recording.At(recordingsRoot, replayName);
            if (!recording.Exists)
            {
                throw new ConfigurationException($"No recording at {recording.Directory}.");
            }

            var header = recording.LoadHeader();
            var head = await git.HeadAsync(config.RepoPath, cancellationToken);
            var sdk = await new DotnetCli(processRunner).SdkVersionAsync(config.RepoPath, cancellationToken);
            if (header.TargetCommit != head)
            {
                throw new ConfigurationException(
                    $"Recording '{replayName}' was made at commit {header.TargetCommit[..8]}; the repo is at {head[..8]}. The recorded patches only apply to the same commit. Record again.");
            }

            if (header.SdkVersion != sdk)
            {
                console.MarkupLine($"[yellow]warning:[/] recorded with SDK {Markup.Escape(header.SdkVersion)}, running with {Markup.Escape(sdk)}. Build output may differ.");
            }

            console.MarkupLine($"[black on yellow] REPLAY [/] [yellow]'{Markup.Escape(replayName)}', recorded {header.RecordedUtc:yyyy-MM-dd HH:mm} UTC. Agent sessions are played back; bumps, builds, tests and guardrails run live.[/]");
            console.WriteLine();
            frozenPlan = header.Plan;
            fixer = new ReplayFixer(recording, console, consoleLock, git, recordReplay.MaxGapSeconds);
        }
        else
        {
            fixer = CreateFixer(agent ?? config.Options.Agent.Provider, config, processRunner, console, consoleLock, prompter, out var activity);
            copilot = fixer as CopilotFixer;
            if (recordReplay.Record is { } recordName && activity is not null)
            {
                fixer = new RecordingFixer(fixer, Recording.At(recordingsRoot, recordName), activity, git);
            }
        }

        await using var ownedFixer = new AsyncDisposableFixer(fixer);
        var renderer = new RunRenderer(console);
        var orchestrator = new RunOrchestrator(config, processRunner, ownedFixer, renderer);
        var report = await orchestrator.RunAsync(only, planFile?.FullName, cancellationToken, frozenPlan);

        // Publish: the PR description always; the push only through push_branch, with the operator's approval.
        var description = PrDescription.Create(report);
        var runOutput = Path.Combine(config.OutputDirectory, $"run-{report.RunId}");
        await File.WriteAllTextAsync(Path.Combine(runOutput, "pr-description.md"), description, CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(config.OutputDirectory, "pr-description.md"), description, CancellationToken.None);
        PushResult? push = null;
        if (report.Ledger.Count > 0 && !cancellationToken.IsCancellationRequested)
        {
            var tool = new PushBranchTool(git, report, dryRun: true);
            renderer.PublishHeader(copilot is not null ? "the agent must call push_branch; you approve it" : "push_branch needs your approval");
            push = copilot is not null
                ? await copilot.PublishAsync(tool, report, cancellationToken)
                : await new DirectPushPublisher(prompter).PublishAsync(tool, cancellationToken);
        }

        renderer.Published(Path.Combine(config.OutputDirectory, "pr-description.md"), push);

        if (recordReplay.Record is { } name)
        {
            var recording = Recording.At(recordingsRoot, name);
            recording.SaveHeader(new RecordingHeader(name, DateTimeOffset.UtcNow, report.TargetCommit, report.SdkVersion, Environment.OSVersion.ToString(), report.Plan));
            console.MarkupLine($"[grey]Recorded to {Markup.Escape(recording.Directory)}[/]");
        }

        return report.Groups.Any(g => g.Status == GroupStatus.Cancelled) ? ExitCodes.Cancelled : ExitCodes.Success;
    });
}

static IGroupFixer CreateFixer(
    string provider, ResolvedConfig config, IProcessRunner processRunner, IAnsiConsole console, object consoleLock, IApprovalPrompter prompter,
    out AgentActivityRenderer? activity)
{
    switch (provider.ToLowerInvariant())
    {
        case "copilot":
            activity = new AgentActivityRenderer(console, consoleLock);
            return new CopilotFixer(config.Options.Agent, processRunner, activity, prompter);
        case "none":
            activity = null;
            return new NoAgentFixer();
        default:
            throw new ConfigurationException($"Unknown agent provider '{provider}'. Use 'copilot' or 'none'.");
    }
}

static async Task<bool> PreflightAsync(IAnsiConsole console, ResolvedConfig config, IProcessRunner processRunner, bool requireCleanRepo, CancellationToken cancellationToken)
{
    var checks = await new TargetPreflight(processRunner).RunAsync(config, requireCleanRepo, cancellationToken);
    PlanRenderer.RenderPreflight(console, checks);
    return checks.All(c => c.Passed);
}

static async Task<int> HandleErrorsAsync(IAnsiConsole console, Func<Task<int>> action)
{
    try
    {
        return await action();
    }
    catch (ConfigurationException ex)
    {
        console.MarkupLine($"[red]Configuration error:[/] {Markup.Escape(ex.Message)}");
        return ExitCodes.ConfigurationError;
    }
    catch (PackageListException ex)
    {
        console.MarkupLine($"[red]{Markup.Escape(ex.Message)}[/]");
        console.WriteLine(ex.RawOutput.Trim());
        return ExitCodes.DetectionFailed;
    }
    catch (RunAbortedException ex)
    {
        console.MarkupLine($"[red]Run aborted:[/] {Markup.Escape(ex.Message)}");
        return ExitCodes.RunAborted;
    }
    catch (GitException ex)
    {
        console.MarkupLine($"[red]git failed:[/] {Markup.Escape(ex.Message)}");
        return ExitCodes.RunAborted;
    }
    catch (OperationCanceledException)
    {
        console.MarkupLine("[yellow]Cancelled.[/]");
        return ExitCodes.Cancelled;
    }
}

/// <summary>Lets the run own the fixer's lifetime whether or not it holds resources (the Copilot runtime does).</summary>
internal sealed class AsyncDisposableFixer(IGroupFixer inner) : IGroupFixer, IAsyncDisposable
{
    public Task<FixOutcome> FixAsync(FixContext context, CancellationToken cancellationToken) => inner.FixAsync(context, cancellationToken);

    public ValueTask DisposeAsync() => inner is IAsyncDisposable disposable ? disposable.DisposeAsync() : ValueTask.CompletedTask;
}

internal sealed record RecordReplay(string? Record, string? Replay, double MaxGapSeconds);

internal static class ExitCodes
{
    public const int Success = 0;
    public const int ConfigurationError = 2;
    public const int PreflightFailed = 3;
    public const int DetectionFailed = 4;
    public const int RunAborted = 5;
    public const int Cancelled = 130;
}
