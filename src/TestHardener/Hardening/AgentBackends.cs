using AgentHarness;
using AgentHarness.Copilot;
using RepoKit.AzureDevOps;
using TestHardener.Config;

namespace TestHardener.Hardening;

internal sealed class AgentUnavailableException(string message) : Exception(message);

internal interface IAgentBackendFactory
{
    IAgentBackend Create();

    Task EnsureReadyAsync(IAgentBackend backend, string workingDirectory, CancellationToken cancellationToken);
}

internal sealed class CopilotBackendFactory(ResolvedConfig config, AzureDevOpsCredentialProvider credentials) : IAgentBackendFactory
{
    private bool _ready;

    public IAgentBackend Create()
    {
        var agent = config.Options.Agent;
        var options = new CopilotOptions
        {
            Model = agent.Model,
            ReasoningEffort = agent.ReasoningEffort,
            GitHubTokenEnvironmentVariable = agent.GitHubTokenEnvVar,
            ClientName = "TestHardener",
        };

        foreach (var name in agent.RemoveEnvironmentVariables.Concat(credentials.SecretEnvironmentVariables)
            .Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            options.HiddenEnvironmentVariables.Add(name);
        }

        return new CopilotBackend(options);
    }

    public async Task EnsureReadyAsync(IAgentBackend backend, string workingDirectory, CancellationToken cancellationToken)
    {
        if (_ready || backend is not CopilotBackend copilot)
        {
            return;
        }

        var status = await copilot.CheckAsync(workingDirectory, cancellationToken);
        if (!status.Ready)
        {
            throw new AgentUnavailableException(status.Message);
        }

        _ready = true;
    }
}
