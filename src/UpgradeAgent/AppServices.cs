using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Spectre.Console;
using UpgradeAgent.Agent;
using UpgradeAgent.Agent.Activities;
using UpgradeAgent.Build;
using UpgradeAgent.Cli;
using UpgradeAgent.Config;
using UpgradeAgent.Detection;
using UpgradeAgent.Guardrails;
using UpgradeAgent.Infrastructure;
using UpgradeAgent.Preflight;
using UpgradeAgent.Publishing;
using UpgradeAgent.Run;
using UpgradeAgent.Ui;

namespace UpgradeAgent;

/// <summary>
/// The composition root: the one place that decides which implementation backs each seam. Everything else
/// receives its collaborators through its constructor. No Generic Host: this is a run-to-completion CLI, and
/// System.CommandLine already owns Ctrl+C and the process lifetime.
/// </summary>
internal static class AppServices
{
    /// <param name="interactive">False with --non-interactive: anything that needs a human is declined.</param>
    public static ServiceProvider Build(string? configPath, IAnsiConsole console, bool interactive, bool verbose)
    {
        var (configuration, baseDirectory) = ConfigLoader.Load(configPath);
        var services = new ServiceCollection();

        services.AddLogging(logging => logging
            .SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Warning)
            .AddSimpleConsole(o => o.SingleLine = true)
            .AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace));

        // Options: bound once, validated before any work starts.
        services.AddOptions<UpgradeAgentOptions>().Bind(configuration).ValidateOnStart();
        services.AddSingleton<IValidateOptions<UpgradeAgentOptions>, UpgradeAgentOptionsValidator>();
        services.AddSingleton(sp => ConfigLoader.Resolve(sp.GetRequiredService<IOptions<UpgradeAgentOptions>>().Value, baseDirectory));
        services.AddSingleton(sp => sp.GetRequiredService<ResolvedConfig>().Options.Target);
        services.AddSingleton(sp => sp.GetRequiredService<ResolvedConfig>().Options.Policy);
        services.AddSingleton(sp => sp.GetRequiredService<ResolvedConfig>().Options.Agent);
        services.AddSingleton(sp => sp.GetRequiredService<ResolvedConfig>().Options.AzureDevOps);

        // Console and output.
        services.AddSingleton(console);
        services.AddSingleton<SynchronizedConsole>();
        services.AddSingleton<PlanRenderer>();
        services.AddSingleton<RunRenderer>();
        services.AddSingleton<IRunProgress>(sp => sp.GetRequiredService<RunRenderer>());
        services.AddSingleton<PublishRenderer>();
        services.AddSingleton<IApprovalPrompter>(sp => interactive && console.Profile.Capabilities.Interactive
            ? new ConsoleApprovalPrompter(sp.GetRequiredService<SynchronizedConsole>())
            : new DeclineAllPrompter());

        // External tools.
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IProcessRunner>(sp => new LoggingProcessRunner(
            new ProcessRunner(), sp.GetRequiredService<ILogger<LoggingProcessRunner>>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<GitCli>();
        services.AddSingleton<DotnetCli>();

        // Detection and planning.
        services.AddSingleton<PackageListRunner>();
        services.AddSingleton<PlanService>();
        services.AddSingleton<TargetPreflight>();

        // Guardrails, in the order they are reported.
        services.AddSingleton<IGuardrail, GitStateGuardrail>();
        services.AddSingleton<IGuardrail, BuildGuardrail>();
        services.AddSingleton<IGuardrail, TestsGuardrail>();
        services.AddSingleton<IGuardrail, SuppressionGuardrail>();
        services.AddSingleton<IGuardrail, BuildSettingsGuardrail>();
        services.AddSingleton<IGuardrail, FilesGuardrail>();
        services.AddSingleton<IReviewNoteSource, TestFileNotes>();
        services.AddSingleton<IReviewNoteSource, PublicApiNotes>();
        services.AddSingleton<IReviewNoteSource, ClaimNotes>();
        services.AddSingleton<GuardrailRunner>();

        // The run.
        services.AddSingleton<BaselineProvider>();
        services.AddSingleton<GroupCommitter>();
        services.AddSingleton<GroupPipeline>();
        services.AddSingleton<RunOrchestrator>();

        // The agent.
        services.AddSingleton(sp => new AgentActivity(new ConsoleActivitySink(sp.GetRequiredService<SynchronizedConsole>())));
        services.AddSingleton<PackageDocsLocator>();
        services.AddSingleton<AgentSetupFactory>();

        // Publishing.
        services.AddSingleton<GitPush>();
        services.AddSingleton(sp => new AzureDevOpsCredentialProvider(sp.GetRequiredService<AzureDevOpsOptions>()));
        services.AddSingleton<AzureDevOpsPublisher>();
        services.AddSingleton<RunPublisher>();

        // Commands.
        services.AddSingleton<PlanCommandHandler>();
        services.AddSingleton<RunCommandHandler>();
        services.AddSingleton<AzureDevOpsCommandHandler>();

        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        provider.GetRequiredService<IStartupValidator>().Validate();
        return provider;
    }
}
