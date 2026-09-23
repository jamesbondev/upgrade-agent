using System.Collections;
using UpgradeAgent.Agent;

namespace UpgradeAgent.Tests.Agent;

public class AgentEnvironmentTests
{
    [Fact]
    public void RemovesSecretsKeepsTheRestAndAppliesOverrides()
    {
        var current = new Hashtable
        {
            ["PATH"] = "/usr/bin",
            ["HOME"] = "/home/me",
            ["SYSTEM_ACCESSTOKEN"] = "x",
            ["ADO_PAT"] = "x",
            ["MY_SERVICE_PAT"] = "x",
            ["AZURE_CLIENT_SECRET"] = "x",
            ["DB_PASSWORD"] = "x",
            ["GITHUB_TOKEN"] = "x",
            ["CUSTOM_THING"] = "x",
            ["MSBUILDDISABLENODEREUSE"] = "0",
        };

        var environment = AgentEnvironment.Build(current, ["CUSTOM_THING"], new Dictionary<string, string?> { ["MSBUILDDISABLENODEREUSE"] = "1" });

        Assert.Equal(["HOME", "MSBUILDDISABLENODEREUSE", "PATH"], environment.Keys.Order());
        Assert.Equal("1", environment["MSBUILDDISABLENODEREUSE"]);
    }

    [Theory]
    [InlineData("PATH", false)]
    [InlineData("DOTNET_ROOT", false)]
    [InlineData("NUGET_PACKAGES", false)]
    [InlineData("VSS_NUGET_EXTERNAL_FEED_ENDPOINTS", true)]
    [InlineData("OPENAI_API_KEY", true)]
    [InlineData("Sql_ConnectionString", true)]
    public void ClassifiesSecrets(string name, bool expected)
    {
        Assert.Equal(expected, AgentEnvironment.IsSecret(name));
    }
}
