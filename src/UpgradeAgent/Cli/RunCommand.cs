using System.CommandLine;
using UpgradeAgent.Agent;
using UpgradeAgent.Config;
using UpgradeAgent.Preflight;
using UpgradeAgent.Publishing;
using UpgradeAgent.Replay;
using UpgradeAgent.Run;
using UpgradeAgent.Ui;

namespace UpgradeAgent.Cli;

internal static class RunCommand
{
    private static readonly Option<FileInfo> Plan = new Option<FileInfo>("--plan") { Description = "Use a saved plan instead of detecting." }.AcceptExistingOnly();
    private static readonly Option<AgentProvider?> Agent = new("--agent") { Description = "Agent that fixes broken groups: copilot or none. Default: Agent:Provider." };
    private static readonly Option<string?> Record = new("--record") { Description = "Record the agent's sessions under recordings/<name> for later replay." };
    private static readonly Option<string?> Replay = new("--replay") { Description = "Replay recordings/<name> instead of calling the agent. Builds, tests and guardrails still run live." };
    private static readonly Option<double> ReplayMaxGap = new("--replay-max-gap") { Description = "Longest pause between replayed agent lines, in seconds.", DefaultValueFactory = _ => 0.8 };
    private static readonly Option<bool> Ado = new("--ado") { Description = "Publish for real: push the branch and open a draft PR in Azure DevOps (default: dry run)." };
    private static readonly Option<bool> NonInteractive = new("--non-interactive") { Description = "Never prompt; anything that needs approval is declined. Implied when output is redirected." };

    public static Command Create()
    {
        var command = new Command("run", "Upgrade packages on a new branch in a worktree beside the repo, one commit per accepted group.")
        {
            CommonOptions.Only, Plan, Agent, NonInteractive, Record, Replay, ReplayMaxGap, Ado,
        };

        // Combinations that can't work are refused while parsing, before any work starts.
        command.Validators.Add(result =>
        {
            if (result.GetValue(Replay) is not null && (result.GetValue(Record) is not null || result.GetValue(Plan) is not null || result.GetValue(Agent) is not null))
            {
                result.AddError("--replay uses the recording's plan and agent sessions; it can't be combined with --record, --plan or --agent.");
            }

            if (result.GetValue(Record) is not null && result.GetValue(Agent) == AgentProvider.None)
            {
                result.AddError("--record needs a live agent; there is nothing to record with --agent none.");
            }
        });

        command.SetAction((parseResult, cancellationToken) => CommandRunner.RunAsync<RunCommandHandler>(
            parseResult,
            handler => handler.RunAsync(
                new RunArguments(
                    parseResult.GetValue(CommonOptions.Only) ?? [],
                    parseResult.GetValue(Plan)?.FullName,
                    new AgentArguments(parseResult.GetValue(Agent), parseResult.GetValue(Record), parseResult.GetValue(Replay), parseResult.GetValue(ReplayMaxGap)),
                    parseResult.GetValue(Ado)),
                cancellationToken),
            interactive: !parseResult.GetValue(NonInteractive)));
        return command;
    }
}

internal sealed record RunArguments(IReadOnlyCollection<string> Only, string? PlanFile, AgentArguments Agent, bool Ado);

internal sealed class RunCommandHandler(
    ResolvedConfig config,
    TargetPreflight preflight,
    PlanRenderer planRenderer,
    AzureDevOpsPublisher azureDevOps,
    AgentSetupFactory agents,
    RunOrchestrator orchestrator,
    RunPublisher publisher,
    PublishRenderer publishRenderer,
    TimeProvider time)
{
    public async Task<int> RunAsync(RunArguments arguments, CancellationToken cancellationToken)
    {
        var checks = await preflight.RunAsync(config, requireCleanRepo: true, cancellationToken);
        planRenderer.Preflight(checks);
        if (!checks.All(c => c.Passed))
        {
            return ExitCodes.PreflightFailed;
        }

        if (arguments.Ado)
        {
            publishRenderer.AzureDevOpsVerified(await azureDevOps.VerifyAsync(cancellationToken));
        }

        var setup = await agents.CreateAsync(arguments.Agent, arguments.PlanFile, cancellationToken);
        await using (setup.Fixer)
        {
            var report = await orchestrator.RunAsync(setup.PlanSource, arguments.Only, setup.Fixer, cancellationToken);
            await publisher.PublishAsync(report, setup.Publisher, arguments.Ado ? azureDevOps : null, cancellationToken);

            if (setup.Recording is { } recording)
            {
                recording.SaveHeader(new RecordingHeader(
                    recording.Name, time.GetUtcNow(), report.TargetCommit, report.SdkVersion, Environment.OSVersion.ToString(), report.Plan));
                publishRenderer.Recorded(recording.Directory);
            }

            return report.Groups.Any(g => g.Status == GroupStatus.Cancelled) ? ExitCodes.Cancelled : ExitCodes.Success;
        }
    }
}
