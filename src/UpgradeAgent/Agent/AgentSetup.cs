using UpgradeAgent.Agent.Activities;
using UpgradeAgent.Agent.Copilot;
using UpgradeAgent.Build;
using UpgradeAgent.Config;
using UpgradeAgent.Detection;
using UpgradeAgent.Infrastructure;
using UpgradeAgent.Publishing;
using UpgradeAgent.Replay;
using UpgradeAgent.Run;
using UpgradeAgent.Ui;

namespace UpgradeAgent.Agent;

/// <summary>How the run command was asked to use the agent.</summary>
/// <param name="Provider">From --agent; null means Agent:Provider.</param>
internal sealed record AgentArguments(AgentProvider? Provider, string? Record, string? Replay, double ReplayMaxGapSeconds);

/// <summary>The fixer, how publishing gets approved, and where the plan comes from, chosen together.</summary>
/// <param name="Recording">Where to save the recording header after the run (--record).</param>
internal sealed record AgentSetup(IGroupFixer Fixer, IPushPublisher Publisher, PlanSource PlanSource, Recording? Recording);

/// <summary>
/// Picks the agent setup for a run: a live provider (optionally recorded), a replay of a recording, or no agent.
/// Every combination produces the same two seams, so the rest of the run doesn't know which one it got.
/// </summary>
internal sealed class AgentSetupFactory(
    ResolvedConfig config,
    GitCli git,
    DotnetCli dotnet,
    PackageDocsLocator docs,
    AgentActivity activity,
    SynchronizedConsole console,
    IApprovalPrompter prompter,
    PublishRenderer renderer,
    TimeProvider time)
{
    public async Task<AgentSetup> CreateAsync(AgentArguments arguments, string? planFile, CancellationToken cancellationToken)
    {
        if (arguments.Replay is { } replay)
        {
            return await CreateReplayAsync(replay, arguments.ReplayMaxGapSeconds, cancellationToken);
        }

        PlanSource planSource = planFile is null ? new PlanSource.Detect() : new PlanSource.FromFile(planFile);
        var agent = config.Options.Agent;
        switch (arguments.Provider ?? agent.Provider)
        {
            case AgentProvider.Copilot:
                var host = new CopilotClientHost(agent, config.Options.AzureDevOps);
                IGroupFixer fixer = new AgentFixRunner(new CopilotBackend(host, agent), agent, docs, activity, prompter, time);
                Recording? recording = null;
                if (arguments.Record is { } name)
                {
                    recording = Recording.At(config.RecordingsDirectory, name);
                    fixer = new RecordingFixer(fixer, recording, activity, git, time);
                }

                return new AgentSetup(fixer, new CopilotPushPublisher(host, agent, prompter), planSource, recording);

            default:
                return new AgentSetup(new NoAgentFixer(), new DirectPushPublisher(prompter), planSource, null);
        }
    }

    private async Task<AgentSetup> CreateReplayAsync(string name, double maxGapSeconds, CancellationToken cancellationToken)
    {
        var recording = Recording.At(config.RecordingsDirectory, name);
        var head = await git.HeadAsync(config.RepoPath, cancellationToken);
        var sdk = await dotnet.SdkVersionAsync(config.RepoPath, cancellationToken);
        var (header, warning) = recording.OpenForReplay(head, sdk);
        renderer.ReplayStarting(header, warning);
        return new AgentSetup(
            new ReplayFixer(recording, activity, console, git, maxGapSeconds, time), new DirectPushPublisher(prompter), new PlanSource.Frozen(header.Plan), null);
    }
}
