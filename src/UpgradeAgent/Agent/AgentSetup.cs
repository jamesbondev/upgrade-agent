using AgentHarness.Copilot;
using AgentHarness.Policies;
using UpgradeAgent.Agent.Activities;
using UpgradeAgent.Build;
using UpgradeAgent.Config;
using UpgradeAgent.Detection;
using UpgradeAgent.Infrastructure;
using UpgradeAgent.Publishing;
using UpgradeAgent.Replay;
using UpgradeAgent.Run;
using UpgradeAgent.Ui;

namespace UpgradeAgent.Agent;

internal sealed record AgentArguments(AgentProvider? Provider, string? Record, string? Replay, double ReplayMaxGapSeconds);

internal sealed record AgentSetup(IGroupFixer Fixer, IPushPublisher Publisher, PlanSource PlanSource, Recording? Recording);

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
                var backend = new CopilotBackend(CopilotOptionsFor(agent, config.Options.AzureDevOps));
                IGroupFixer fixer = new AgentFixRunner(backend, agent, docs, activity, prompter, time);
                Recording? recording = null;
                if (arguments.Record is { } name)
                {
                    recording = Recording.At(config.RecordingsDirectory, name);
                    fixer = new RecordingFixer(fixer, recording, activity, git, time);
                }

                return new AgentSetup(fixer, new AgentPushPublisher(backend, prompter), planSource, recording);

            default:
                return new AgentSetup(new NoAgentFixer(), new DirectPushPublisher(prompter), planSource, null);
        }
    }

    internal static CopilotOptions CopilotOptionsFor(AgentOptions agent, AzureDevOpsOptions azureDevOps)
    {
        var options = new CopilotOptions
        {
            Model = agent.Model,
            ReasoningEffort = agent.ReasoningEffort,
            GitHubTokenEnvironmentVariable = agent.GitHubTokenEnvVar,
            ClientName = "UpgradeAgent",
        };

        foreach (var name in agent.RemoveEnvironmentVariables.Append(azureDevOps.PatEnvVar).Append(azureDevOps.AccessTokenEnvVar))
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                options.HiddenEnvironmentVariables.Add(name);
            }
        }

        foreach (var (name, value) in DotnetCli.BaseEnvironment)
        {
            options.EnvironmentOverrides[name] = value;
        }

        return options;
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
