using UpgradeAgent.Agent;
using UpgradeAgent.Build;
using UpgradeAgent.Config;

namespace UpgradeAgent.Tests.Agent;

public class AgentSetupTests
{
    [Fact]
    public void CopilotHidesTheConfiguredAndAzureDevOpsSecretsAndUsesTheAppsDotnetSettings()
    {
        var agent = new AgentOptions
        {
            Model = "claude-sonnet-4.5",
            ReasoningEffort = "high",
            GitHubTokenEnvVar = "COPILOT_PIPELINE_TOKEN",
            RemoveEnvironmentVariables = ["INTERNAL_FEED", ""],
        };
        var azureDevOps = new AzureDevOpsOptions { PatEnvVar = "MY_PAT", AccessTokenEnvVar = "" };

        var options = AgentSetupFactory.CopilotOptionsFor(agent, azureDevOps);

        Assert.Equal(("claude-sonnet-4.5", "high", "COPILOT_PIPELINE_TOKEN", "UpgradeAgent"),
            (options.Model, options.ReasoningEffort, options.GitHubTokenEnvironmentVariable, options.ClientName));
        Assert.Equal(["INTERNAL_FEED", "MY_PAT"], options.HiddenEnvironmentVariables);
        Assert.Equal(DotnetCli.BaseEnvironment.OrderBy(e => e.Key), options.EnvironmentOverrides.OrderBy(e => e.Key));
    }
}
