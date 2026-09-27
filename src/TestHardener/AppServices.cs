using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RepoKit;
using RepoKit.AzureDevOps;
using Spectre.Console;
using TestHardener.Cli;
using TestHardener.Config;
using TestHardener.Hardening;
using TestHardener.Infrastructure;
using TestHardener.Publishing;
using TestHardener.Run;
using TestHardener.Stryker;
using TestHardener.Ui;

namespace TestHardener;

internal static class AppServices
{
    public static ServiceProvider Build(string? configPath, IAnsiConsole console, bool verbose)
    {
        var (configuration, baseDirectory) = ConfigLoader.Load(configPath);
        var services = new ServiceCollection();

        services.AddLogging(logging => logging
            .SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Warning)
            .AddSimpleConsole(o => o.SingleLine = true)
            .AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace));

        services.AddOptions<TestHardenerOptions>().Bind(configuration).ValidateOnStart();
        services.AddSingleton<IValidateOptions<TestHardenerOptions>, OptionsValidator>();
        services.AddSingleton(sp => ConfigLoader.Resolve(sp.GetRequiredService<IOptions<TestHardenerOptions>>().Value, baseDirectory));

        services.AddSingleton(console);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IProcessRunner>(sp => new LoggingProcessRunner(
            new ProcessRunner(), sp.GetRequiredService<ILogger<LoggingProcessRunner>>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<GitCli>();
        services.AddSingleton(sp => new AzureDevOpsCredentialProvider(sp.GetRequiredService<ResolvedConfig>().Options.AzureDevOps.ToAuthOptions()));

        services.AddSingleton<StrykerTool>();
        services.AddSingleton<IStrykerRunner, StrykerRunner>();
        services.AddSingleton<ISurveyProgress, ConsoleSurveyProgress>();
        services.AddSingleton<RepoSurveyor>();
        services.AddSingleton<SurveyOrchestrator>();
        services.AddSingleton<SurveyCommandHandler>();

        services.AddSingleton(sp => sp.GetRequiredService<ResolvedConfig>().Options.Hardening);
        services.AddSingleton<DotnetCli>();
        services.AddSingleton<IGroupVerifier, GroupVerifier>();
        services.AddSingleton<IGroupHardener, GroupHardener>();
        services.AddSingleton<IAgentBackendFactory, CopilotBackendFactory>();
        services.AddSingleton<IHardenProgress, ConsoleHardenProgress>();
        services.AddSingleton<HttpClient>();
        services.AddSingleton<IPullRequestHosts>(sp => new AzureDevOpsPullRequestHosts(
            sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<ResolvedConfig>().Options.Publish));
        services.AddSingleton<HardenOrchestrator>();
        services.AddSingleton<HardenCommandHandler>();

        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        provider.GetRequiredService<IStartupValidator>().Validate();
        return provider;
    }
}
