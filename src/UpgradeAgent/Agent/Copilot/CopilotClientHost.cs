using GitHub.Copilot;
using UpgradeAgent.Build;
using UpgradeAgent.Config;

namespace UpgradeAgent.Agent.Copilot;

/// <summary>
/// Owns the Copilot runtime: one <see cref="CopilotClient"/> per run, started lazily in the run's worktree,
/// shared by the fix sessions and the publish session.
/// </summary>
internal sealed class CopilotClientHost(AgentOptions agent, AzureDevOpsOptions azureDevOps) : IAsyncDisposable
{
    private readonly SemaphoreSlim _starting = new(1, 1);
    private CopilotClient? _client;
    private string? _worktree;

    public async Task<CopilotClient> GetAsync(string worktree, CancellationToken cancellationToken)
    {
        await _starting.WaitAsync(cancellationToken);
        try
        {
            if (_client is not null && _worktree == worktree)
            {
                return _client;
            }

            await StopAsync();
            _client = new CopilotClient(new CopilotClientOptions
            {
                WorkingDirectory = worktree,
                Environment = AgentEnvironment.Build(Environment.GetEnvironmentVariables(), HiddenVariables(), DotnetCli.BaseEnvironment),
                GitHubToken = string.IsNullOrWhiteSpace(agent.GitHubTokenEnvVar) ? null : Environment.GetEnvironmentVariable(agent.GitHubTokenEnvVar),
            });
            _worktree = worktree;
            await _client.StartAsync(cancellationToken);
            return _client;
        }
        finally
        {
            _starting.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _starting.Dispose();
    }

    /// <summary>
    /// On top of the built-in secret patterns: the configured extras, the variable holding Copilot's own token,
    /// and the Azure DevOps credentials, whatever they are called. The agent must never see any of them.
    /// </summary>
    private IEnumerable<string> HiddenVariables() =>
        agent.RemoveEnvironmentVariables
            .Append(azureDevOps.PatEnvVar)
            .Append(azureDevOps.AccessTokenEnvVar)
            .Concat(string.IsNullOrWhiteSpace(agent.GitHubTokenEnvVar) ? [] : [agent.GitHubTokenEnvVar]);

    private async Task StopAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
            _client = null;
            _worktree = null;
        }
    }
}
