using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReadmeChecker.Agent;
using ReadmeChecker.Cli;
using ReadmeChecker.Config;
using ReadmeChecker.Fixing;
using ReadmeChecker.Infrastructure;
using ReadmeChecker.Publishing;
using ReadmeChecker.Run;
using ReadmeChecker.Ui;
using RepoKit;
using RepoKit.AzureDevOps;
using Spectre.Console;

namespace ReadmeChecker;

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

        services.AddOptions<ReadmeCheckerOptions>().Bind(configuration).ValidateOnStart();
        services.AddSingleton<IValidateOptions<ReadmeCheckerOptions>, OptionsValidator>();
        services.AddSingleton(sp => ConfigLoader.Resolve(sp.GetRequiredService<IOptions<ReadmeCheckerOptions>>().Value, baseDirectory));
        services.AddSingleton(sp => sp.GetRequiredService<ResolvedConfig>().Options.Agent);

        services.AddSingleton(console);
        services.AddSingleton<ICheckProgress, ConsoleCheckProgress>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IProcessRunner>(sp => new LoggingProcessRunner(
            new ProcessRunner(), sp.GetRequiredService<ILogger<LoggingProcessRunner>>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<GitCli>();
        services.AddSingleton(sp => new AzureDevOpsCredentialProvider(sp.GetRequiredService<ResolvedConfig>().Options.AzureDevOps.ToAuthOptions()));

        services.AddSingleton<IAgentBackendFactory, CopilotBackendFactory>();
        services.AddSingleton<ReadmeAssessor>();
        services.AddSingleton<DeepReadmeAssessor>();
        services.AddSingleton<IReadmeFixer, ReadmeFixer>();
        services.AddSingleton(sp => new RepoInspector(
            sp.GetRequiredService<ResolvedConfig>(),
            sp.GetRequiredService<GitCli>(),
            sp.GetRequiredService<AzureDevOpsCredentialProvider>(),
            sp.GetRequiredService<ReadmeAssessor>(),
            sp.GetRequiredService<DeepReadmeAssessor>(),
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<CheckOrchestrator>();
        services.AddSingleton<CheckCommandHandler>();

        services.AddSingleton<HttpClient>();
        services.AddSingleton<IPullRequestHosts>(sp => new AzureDevOpsPullRequestHosts(
            sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<ResolvedConfig>().Options.Publish));
        services.AddSingleton<IFixProgress, ConsoleFixProgress>();
        services.AddSingleton<FixOrchestrator>();
        services.AddSingleton<FixCommandHandler>();

        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        provider.GetRequiredService<IStartupValidator>().Validate();
        return provider;
    }
}
