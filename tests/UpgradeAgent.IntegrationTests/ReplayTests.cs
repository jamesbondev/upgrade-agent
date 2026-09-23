using System.Text.Json;

namespace UpgradeAgent.IntegrationTests;

public sealed class ReplayTests(FixtureEnvironment fixture) : IClassFixture<FixtureEnvironment>
{
    [Fact]
    public void RecordedFixIsAcceptedAndCommitted()
    {
        var (exitCode, output, report, worktree) = fixture.Replay("fixture");

        Assert.True(exitCode == 0, output);
        Assert.Equal(["accepted", "accepted"], Statuses(report));
        Assert.Contains("REPLAY", output, StringComparison.Ordinal);
        Assert.Equal(2, report.GetProperty("ledger").GetArrayLength());

        var props = File.ReadAllText(Path.Combine(worktree, "Directory.Packages.props"));
        Assert.Contains("\"Fixture.Lib\" Version=\"2.0.0\"", props, StringComparison.Ordinal);
        Assert.Contains("GetConfigAsync", File.ReadAllText(Path.Combine(worktree, "src", "LoanLedger", "InterestCalculator.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void CheatingFixIsRejectedDespiteGreenBuildAndTests()
    {
        var (exitCode, output, report, worktree) = fixture.Replay("fixture-cheat");

        Assert.True(exitCode == 0, output);
        Assert.Equal(["accepted", "rejected"], Statuses(report));

        var reason = report.GetProperty("groups")[1].GetProperty("reason").GetString()!;
        Assert.Contains("#pragma warning disable", reason, StringComparison.Ordinal);
        Assert.Contains("Skip =", reason, StringComparison.Ordinal);
        Assert.Contains("ToJson_ContainsAllFields", reason, StringComparison.Ordinal);

        // Reverted: the worktree holds only the accepted patch/minor commit.
        Assert.Contains("\"Fixture.Lib\" Version=\"1.1.0\"", File.ReadAllText(Path.Combine(worktree, "Directory.Packages.props")), StringComparison.Ordinal);
        Assert.DoesNotContain("#pragma", File.ReadAllText(Path.Combine(worktree, "src", "LoanLedger", "PortfolioSummary.cs")), StringComparison.Ordinal);
    }

    private static string[] Statuses(JsonElement report) =>
        report.GetProperty("groups").EnumerateArray().Select(g => g.GetProperty("status").GetString()!).ToArray();
}
