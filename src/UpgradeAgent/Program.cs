using System.CommandLine;
using System.Text.Json;
using Spectre.Console;
using UpgradeAgent;
using UpgradeAgent.Agent;
using UpgradeAgent.Config;
using UpgradeAgent.Detection;
using UpgradeAgent.Infrastructure;
using UpgradeAgent.Preflight;
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
};
runCommand.SetAction((parseResult, cancellationToken) => RunUpgradeAsync(
    parseResult.GetValue(configOption),
    parseResult.GetValue(onlyOption) ?? [],
    parseResult.GetValue(planOption),
    parseResult.GetValue(agentOption),
    parseResult.GetValue(nonInteractiveOption),
    cancellationToken));

var root = new RootCommand("UpgradeAgent: keeps a .NET repo's NuGet packages up to date, with an AI agent fixing breaking changes.")
{
    planCommand,
    runCommand,
};

return await root.Parse(args).InvokeAsync();

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
    FileInfo? configFile, string[] only, FileInfo? planFile, string? agent, bool nonInteractive, CancellationToken cancellationToken)
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

        await using var fixer = CreateFixer(agent ?? config.Options.Agent.Provider, config, processRunner, console, consoleLock, prompter);
        var orchestrator = new RunOrchestrator(config, processRunner, fixer, new RunRenderer(console));
        var report = await orchestrator.RunAsync(only, planFile?.FullName, cancellationToken);
        return report.Groups.Any(g => g.Status == GroupStatus.Cancelled) ? ExitCodes.Cancelled : ExitCodes.Success;
    });
}

static AsyncDisposableFixer CreateFixer(
    string provider, ResolvedConfig config, IProcessRunner processRunner, IAnsiConsole console, object consoleLock, IApprovalPrompter prompter) =>
    provider.ToLowerInvariant() switch
    {
        "copilot" => new AsyncDisposableFixer(new CopilotFixer(
            config.Options.Agent, processRunner, new AgentActivityRenderer(console, consoleLock), prompter)),
        "none" => new AsyncDisposableFixer(new NoAgentFixer()),
        _ => throw new ConfigurationException($"Unknown agent provider '{provider}'. Use 'copilot' or 'none'."),
    };

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

internal static class ExitCodes
{
    public const int Success = 0;
    public const int ConfigurationError = 2;
    public const int PreflightFailed = 3;
    public const int DetectionFailed = 4;
    public const int RunAborted = 5;
    public const int Cancelled = 130;
}
