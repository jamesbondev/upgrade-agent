using AgentHarness;
using AgentHarness.Copilot;
using ReadmeChecker.Config;
using RepoKit.AzureDevOps;

namespace ReadmeChecker.Agent;

internal sealed class AgentUnavailableException(string message) : Exception(message);

internal interface IAgentBackendFactory
{
    IAgentBackend Create();

    Task EnsureReadyAsync(IAgentBackend backend, string workingDirectory, CancellationToken cancellationToken);
}

internal sealed class CopilotBackendFactory(AgentOptions agent, AzureDevOpsCredentialProvider credentials) : IAgentBackendFactory
{
    private bool _ready;

    public IAgentBackend Create() => new CopilotBackend(CopilotOptionsFor(agent, credentials.SecretEnvironmentVariables));

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

    internal static CopilotOptions CopilotOptionsFor(AgentOptions agent, IEnumerable<string> secretEnvironmentVariables)
    {
        var options = new CopilotOptions
        {
            Model = agent.Model,
            ReasoningEffort = agent.ReasoningEffort,
            GitHubTokenEnvironmentVariable = agent.GitHubTokenEnvVar,
            ClientName = "ReadmeChecker",
        };

        foreach (var name in agent.RemoveEnvironmentVariables.Concat(secretEnvironmentVariables).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            options.HiddenEnvironmentVariables.Add(name);
        }

        return options;
    }
}
