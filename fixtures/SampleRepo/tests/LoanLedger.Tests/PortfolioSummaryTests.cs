namespace LoanLedger.Tests;

public class PortfolioSummaryTests
{
    [Fact]
    public void TotalBalance_SumsAccounts()
    {
        Assert.Equal(260_000m, new PortfolioSummary(TestData.Directory()).TotalBalance(["ACC-1", "ACC-2", "ACC-3"]));
    }

    [Fact]
    public void Describe_FormatsCountAndTotal()
    {
        Assert.Equal("2 accounts, total 260,000.00", new PortfolioSummary(TestData.Directory()).Describe(["ACC-1", "ACC-2"]));
    }

    [Fact]
    public void TotalBalance_ThrowsForUnknownAccount()
    {
        Assert.Throws<KeyNotFoundException>(() => new PortfolioSummary(TestData.Directory()).TotalBalance(["ACC-1", "NOPE"]));
    }
}
